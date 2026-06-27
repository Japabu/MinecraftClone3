// Deferred composition. Ported from Composition.vs/.fs. A vertex-less fullscreen triangle reconstructs world
// position from the G-buffer depth, combines baked block + sky light with the resolved sun shadow, draws a
// procedural Minecraft-style sky for background (cleared far-plane) pixels, shades water (animated normals +
// Fresnel sky reflection + sun/moon specular), and fades everything into the horizon colour with distance fog.
// Output is linear HDR (rgba16float); tonemapping happens in a later pass.
//
// HARD CONTRACTS vs. the GL original:
//   * REVERSE-Z: the G-buffer depth is already WebGPU clip-space z in [0,1]; it is used AS-IS (no depth*2-1).
//   * The depth target is Depth32float, bound as texture_depth_2d and read with textureLoad(integer coords).
//   * NDC reconstruction follows Tonemap.wgsl's UV orientation (uv passed as a varying); uViewProjectionInv is
//     supplied by C# already built for reverse-Z / this UV convention, so ndcXY = uv*2-1 as in the GL source.

// ---------------------------------------------------------------------------------------------------------
// group(0) binding(0): all scalar/vector/matrix uniforms, std140-style layout.
//
// FIELD ORDER + BYTE OFFSETS (the C# mirror MUST match this byte-for-byte):
//   offset  size  field
//      0     64   uViewProjectionInv : mat4x4<f32>
//     64     64   uView              : mat4x4<f32>
//    128     12   uSunColor          : vec3<f32>
//    140      4   uShadowDistance    : f32        (fills uSunColor's vec3 tail padding)
//    144     12   uSkyAmbient        : vec3<f32>
//    156      4   uSunFade           : f32
//    160     12   uMinLight          : vec3<f32>
//    172      4   uShadowStrength    : f32
//    176     12   uAmbientFloor      : vec3<f32>
//    188      4   uShadowsEnabled    : f32
//    192     12   uCameraPos         : vec3<f32>
//    204      4   uDebugShadow       : f32
//    208     12   uSunDirection      : vec3<f32>
//    220      4   uTime              : f32
//    224     12   uMoonColor         : vec3<f32>
//    236      4   uMoonFade          : f32
//    240     12   uSkyColor          : vec3<f32>
//    252      4   uStarBrightness    : f32
//    256     12   uHorizonColor      : vec3<f32>
//    268      4   uSunSize           : f32
//    272     12   uVoidColor         : vec3<f32>
//    284      4   uMoonSize          : f32
//    288     12   uSunsetColor       : vec3<f32>
//    300      4   uSkyDistance       : f32
//    304      4   uFogStart          : f32
//    308      4   uFogEnd            : f32
//    312      8   _pad0              : vec2<f32>
//    320     12   uUnderwaterColor   : vec3<f32>
//    332      4   uUnderwater        : f32
//   total size = 336 bytes
// ---------------------------------------------------------------------------------------------------------
struct CompositionParams {
    uViewProjectionInv: mat4x4<f32>,
    uView: mat4x4<f32>,
    uSunColor: vec3<f32>,
    uShadowDistance: f32,
    uSkyAmbient: vec3<f32>,
    uSunFade: f32,
    uMinLight: vec3<f32>,
    uShadowStrength: f32,
    uAmbientFloor: vec3<f32>,
    uShadowsEnabled: f32,
    uCameraPos: vec3<f32>,
    uDebugShadow: f32,
    uSunDirection: vec3<f32>,
    uTime: f32,
    uMoonColor: vec3<f32>,
    uMoonFade: f32,
    uSkyColor: vec3<f32>,
    uStarBrightness: f32,
    uHorizonColor: vec3<f32>,
    uSunSize: f32,
    uVoidColor: vec3<f32>,
    uMoonSize: f32,
    uSunsetColor: vec3<f32>,
    uSkyDistance: f32,
    uFogStart: f32,
    uFogEnd: f32,
    _pad0: vec2<f32>,
    uUnderwaterColor: vec3<f32>,
    uUnderwater: f32,
};
@group(0) @binding(0) var<uniform> params: CompositionParams;

// group(1): the G-buffer + sky textures and two samplers.
//   binding(0)  uDiffuse        texture_2d<f32>     (Rgba8Unorm)
//   binding(1)  uNormal         texture_2d<f32>     (Rgba8Unorm)
//   binding(2)  uDepth          texture_depth_2d    (Depth32float, read via textureLoad)
//   binding(3)  uLight          texture_2d<f32>     (Rgba8Unorm)
//   binding(4)  uShadowResolved texture_2d<f32>     (half-res r=shadow, g=normDepth)
//   binding(5)  uSunTexture     texture_2d<f32>     (sun.png)
//   binding(6)  uMoonTexture    texture_2d<f32>     (full_moon.png)
//   binding(7)  gbufferSampler  sampler             (GpuSamplers.Framebuffer: nearest/clamp)
//   binding(8)  celestialSampler sampler            (GpuSamplers.Celestial: nearest/clamp)
@group(1) @binding(0) var uDiffuse: texture_2d<f32>;
@group(1) @binding(1) var uNormal: texture_2d<f32>;
@group(1) @binding(2) var uDepth: texture_depth_2d;
@group(1) @binding(3) var uLight: texture_2d<f32>;
@group(1) @binding(4) var uShadowResolved: texture_2d<f32>;
@group(1) @binding(5) var uSunTexture: texture_2d<f32>;
@group(1) @binding(6) var uMoonTexture: texture_2d<f32>;
@group(1) @binding(7) var gbufferSampler: sampler;
@group(1) @binding(8) var celestialSampler: sampler;

// --- tunables (const in the GL source) -------------------------------------------------------------------
const DepthSharpness: f32 = 256.0;
const WaterFlagLo: f32 = 0.7;
const WaterFlagHi: f32 = 0.8;
const WaveAmp: f32 = 0.045;     // base wavelet slope amplitude (octaves below add to it)
const WaveFreq: f32 = 0.55;     // base wavelet spatial frequency (~11-block swells)
const WaveSpeed: f32 = 1.1;     // base scroll speed
const WaterF0: f32 = 0.02;      // water's normal-incidence reflectance (Schlick F0)
const WaterSpecExp: f32 = 200.0;
const WaterSpecGain: f32 = 3.0;

struct VsOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) vid: u32) -> VsOut {
    // Identical fullscreen triangle to Tonemap.wgsl: uv 0..2, clip -1..3 — same UV orientation so sampling
    // the G-buffer here lines up with how the geometry pass wrote it (no flip).
    var o: VsOut;
    let uv = vec2<f32>(f32((vid << 1u) & 2u), f32(vid & 2u));
    o.uv = uv;
    o.clip = vec4<f32>(uv * 2.0 - 1.0, 0.0, 1.0);
    return o;
}

fn sampleTex(t: texture_2d<f32>, uv: vec2<f32>) -> vec4<f32> {
    // Fullscreen, no mips: an explicit-LOD sample avoids implicit-derivative rules in non-uniform control flow.
    return textureSampleLevel(t, gbufferSampler, uv, 0.0);
}

// REVERSE-Z: depth is WebGPU clip-space z in [0,1] already, used as-is. ndcXY follows the GL source (uv*2-1)
// and Tonemap's UV orientation; uViewProjectionInv is supplied for reverse-Z.
fn PositionFromDepth(uv: vec2<f32>, depth: f32) -> vec3<f32> {
    // ndc.y is negated vs uv: the uv basis (used to sample the G-buffer) runs y-down relative to clip-space y,
    // so reconstructing with a plain uv*2-1 yields a vertically-mirrored world position.
    let ndc = vec2<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    let clipSpace = vec4<f32>(ndc, depth, 1.0);
    let homogenousCoord = params.uViewProjectionInv * clipSpace;
    return homogenousCoord.xyz / homogenousCoord.w;
}

fn hash13(p3in: vec3<f32>) -> f32 {
    var p3 = fract(p3in * 0.1031);
    p3 = p3 + dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

// Sparse round stars from a hashed direction grid: each cell may hold one star at a random sub-cell offset.
fn Stars(dir: vec3<f32>) -> f32 {
    let p = dir * 110.0;
    let cell = floor(p);
    let rnd = hash13(cell);
    if (rnd < 0.972) { return 0.0; }
    let center = vec3<f32>(hash13(cell + 11.5), hash13(cell + 23.7), hash13(cell + 41.3));
    let d = length(fract(p) - center);
    return smoothstep(0.13, 0.0, d) * ((rnd - 0.972) / 0.028);
}

// A textured celestial billboard (sun/moon) addressed by the angle between the view ray and its direction.
// Returns the [0,1] quad uv in .xy and 1 in .w when the ray hits the quad (.w 0 = miss / behind the camera).
fn CelestialBillboard(dir: vec3<f32>, toBody: vec3<f32>, size: f32) -> vec4<f32> {
    let along = dot(dir, toBody);
    if (along <= 0.0) { return vec4<f32>(0.0); }
    let right = normalize(cross(vec3<f32>(0.0, 1.0, 0.0), toBody));
    let up = cross(toBody, right);
    let proj = dir / along;            // where the ray pierces the tangent plane one unit out along toBody
    let u = dot(proj, right);
    let v = dot(proj, up);
    if (abs(u) > size || abs(v) > size) { return vec4<f32>(0.0); }
    return vec4<f32>(vec2<f32>(u, v) / (2.0 * size) + 0.5, 0.0, 1.0);
}

// Animated water surface normal. Six directional sine wavelets summed as octaves — each step rotates the
// travel direction by a non-harmonic angle and scales frequency up / amplitude down, so the slopes never tile
// into a visible grid. The surface height is never needed; its analytic xz-gradient is exactly the normal.
fn WaveNormal(p: vec2<f32>, t: f32) -> vec3<f32> {
    let rot = mat2x2<f32>(0.78, -0.62, 0.62, 0.78);   // ~38.5deg per octave: irrational so directions don't repeat
    var dir = normalize(vec2<f32>(0.9, 0.35));
    var amp = WaveAmp;
    var freq = WaveFreq;
    var speed = WaveSpeed;
    var grad = vec2<f32>(0.0);
    for (var i = 0; i < 6; i = i + 1) {
        let phase = dot(p, dir) * freq + t * speed;
        // d/dp of (amp * sin(phase)) = amp * freq * cos(phase) * dir
        grad = grad + dir * (amp * freq * cos(phase));
        dir = rot * dir;
        amp = amp * 0.62;
        freq = freq * 1.74;
        speed = speed * 1.18;
    }
    return normalize(vec3<f32>(-grad.x, 1.0, -grad.y));
}

// The Minecraft-style sky in a given direction: time-of-day gradient + sunrise/sunset glow + stars + textured
// sun/moon billboards. Used for the background and (via reflect) for water so it mirrors the actual sky.
fn SkyColor(dir: vec3<f32>) -> vec3<f32> {
    let h = dir.y;

    // Vertical gradient: void below the horizon, horizon haze at h=0, zenith colour overhead.
    var sky = mix(params.uHorizonColor, params.uSkyColor, smoothstep(0.0, 0.55, h));
    sky = mix(sky, params.uVoidColor, smoothstep(0.0, -0.12, h));

    // Sunrise/sunset orange, concentrated near the horizon in the sun's azimuth.
    let hd = normalize(vec3<f32>(dir.x, 0.0, dir.z) + 1e-5);
    let sd = normalize(vec3<f32>(params.uSunDirection.x, 0.0, params.uSunDirection.z) + 1e-5);
    let glow = pow(max(dot(hd, sd), 0.0), 4.0) * exp(-abs(h) * 6.0);
    sky = sky + params.uSunsetColor * glow;

    // Stars (faded out into the horizon haze and during the day).
    if (params.uStarBrightness > 0.001) {
        sky = sky + vec3<f32>(Stars(dir)) * params.uStarBrightness * smoothstep(-0.05, 0.25, h);
    }

    // Sun, hidden once it dips below its horizon.
    let sunVis = smoothstep(-0.08, 0.06, params.uSunDirection.y);
    if (sunVis > 0.0) {
        let b = CelestialBillboard(dir, params.uSunDirection, params.uSunSize);
        if (b.w > 0.0) {
            let sunCol = textureSampleLevel(uSunTexture, celestialSampler, b.xy, 0.0).rgb * params.uSunColor;
            sky = sky + sunCol * sunVis;
        }
    }

    // Moon, opposite the sun.
    let toMoon = -params.uSunDirection;
    let moonVis = smoothstep(-0.08, 0.06, toMoon.y);
    if (moonVis > 0.0) {
        let b = CelestialBillboard(dir, toMoon, params.uMoonSize);
        if (b.w > 0.0) {
            let moonCol = textureSampleLevel(uMoonTexture, celestialSampler, b.xy, 0.0).rgb;
            sky = sky + moonCol * moonVis;
        }
    }

    return sky;
}

// Depth-aware 2x2 upsample of the half-res shadow: bilinear weights modulated by how close each half-res tap's
// stored depth is to this pixel's, so foreground shadow doesn't leak onto background across an edge.
fn UpsampleShadow(uv: vec2<f32>, fragNormDepth: f32) -> f32 {
    let halfSize = vec2<f32>(textureDimensions(uShadowResolved, 0));
    let coord = uv * halfSize - 0.5;
    let base = floor(coord);
    let f = coord - base;

    var sumShadow = 0.0;
    var sumWeight = 0.0;
    let maxCoord = vec2<i32>(halfSize) - vec2<i32>(1, 1);
    for (var dy = 0; dy < 2; dy = dy + 1) {
        for (var dx = 0; dx < 2; dx = dx + 1) {
            let t = clamp(vec2<i32>(base) + vec2<i32>(dx, dy), vec2<i32>(0, 0), maxCoord);
            let s = textureLoad(uShadowResolved, t, 0).rg;
            let bx = select(f.x, 1.0 - f.x, dx == 0);
            let by = select(f.y, 1.0 - f.y, dy == 0);
            let bilinear = bx * by;
            let depthWeight = exp(-abs(s.g - fragNormDepth) * DepthSharpness);
            // Tiny floor so a pixel whose every tap is a depth mismatch (thin geometry) still falls back to a
            // plain bilinear sample instead of dividing by zero.
            let w = bilinear * (depthWeight + 0.003);
            sumShadow = sumShadow + s.r * w;
            sumWeight = sumWeight + w;
        }
    }
    return select(1.0, sumShadow / sumWeight, sumWeight > 0.0);
}

// Distance fog: melt geometry into the horizon colour at the render-distance edge.
fn ApplyFog(color: vec3<f32>, viewDepth: f32) -> vec3<f32> {
    let fog = clamp((viewDepth - params.uFogStart) / max(params.uFogEnd - params.uFogStart, 1.0), 0.0, 1.0);
    return mix(color, params.uHorizonColor, fog * fog);
}

// Underwater murk: when the camera's eye is inside a liquid block (uUnderwater = 1) the whole scene and the sky
// fog into uUnderwaterColor over a short distance (dense water), keeping a slight permanent tint near the
// camera. uUnderwaterColor is already dimmed by the daylight on the CPU side. Minecraft's "you're underwater" look.
fn ApplyUnderwater(color: vec3<f32>, viewDepth: f32) -> vec3<f32> {
    let fog = max(clamp((viewDepth - 0.5) / (24.0 - 0.5), 0.0, 1.0), 0.12);
    return mix(color, params.uUnderwaterColor, fog);
}

struct ColorResult {
    color: vec4<f32>,
    discard_: bool,
};

fn GetColor(uv: vec2<f32>, worldPos: vec3<f32>, viewDepth: f32) -> ColorResult {
    var r: ColorResult;
    r.discard_ = false;

    let diffuse = sampleTex(uDiffuse, uv);
    if (diffuse.a == 0.0) {
        r.discard_ = true;
        return r;
    }

    let normal = sampleTex(uNormal, uv);
    // If w value of normal is 1 dont apply lighting (eg. bounding boxes).
    if (normal.w == 1.0) {
        r.color = diffuse;
        return r;
    }

    let lightSample = sampleTex(uLight, uv);

    // Sample the resolved shadow only where the directional sun term can matter (same early-outs the resolve
    // pass used): daytime, shadows enabled, a sky-reached surface, within the shadow distance. Else fully lit.
    var shadow = 1.0;
    if (params.uShadowsEnabled > 0.5 && params.uSunFade > 0.0 && lightSample.a > 0.004 && viewDepth < params.uShadowDistance) {
        shadow = UpsampleShadow(uv, viewDepth / params.uShadowDistance);
    }

    if (params.uDebugShadow > 0.5) {
        r.color = vec4<f32>(vec3<f32>(shadow), 1.0);
        return r;
    }

    // Lift the shadow floor by uShadowStrength so a fully-shadowed surface keeps some direct sun. Only the
    // direct-sun term is affected.
    let litShadow = mix(1.0, shadow, params.uShadowStrength);

    // Sky-exposed light: direct sun (shadowed, daytime) plus ambient sky fill / moonlight (unshadowed) -- both
    // gated by the baked sky factor. Block light (torches) is separate and reaches caves; brighter wins.
    let skyLight = lightSample.a * (litShadow * params.uSunColor * params.uSunFade + params.uSkyAmbient);
    let light = max(max(lightSample.rgb, skyLight), params.uAmbientFloor);

    let baseColor = diffuse.rgb * max(light, params.uMinLight);

    // Water surface (mesher-flagged normal.w ~ 0.75). The transparent pass already alpha-blended the water tint
    // over whatever lies beneath into baseColor; here we add the view-dependent surface optics a deferred
    // G-buffer can't bake: a Fresnel-weighted reflection of the live procedural sky plus sharp sun/moon specular
    // glints, all riding the animated wave normal. Every term scales by the baked sky factor (lightSample.a),
    // so roofed-over / cave water falls back to the plain tint with no special-casing.
    if (normal.w > WaterFlagLo && normal.w < WaterFlagHi) {
        let skyFac = lightSample.a;
        let V = normalize(params.uCameraPos - worldPos);

        // Only the upward-facing top surface ripples; vertical water faces keep their flat geometric normal.
        let faceN = normalize(normal.xyz * 2.0 - 1.0);
        var N = faceN;
        if (faceN.y > 0.5) { N = WaveNormal(worldPos.xz, params.uTime); }

        // Schlick Fresnel: grazing views mirror the sky, top-down views look into the water.
        let fresnel = WaterF0 + (1.0 - WaterF0) * pow(1.0 - max(dot(N, V), 0.0), 5.0);

        // Reflect the live sky down the mirror ray. Any ray the waves bend below the horizon is lifted back to a
        // grazing direction so the reflection reads as sky/haze rather than the void colour beneath the horizon.
        var refl = reflect(-V, N);
        refl.y = max(refl.y, 0.04);
        let reflection = SkyColor(normalize(refl));

        // Sun + moon specular highlights (Blinn-Phong on the rippled normal). The sun glint is shadow-tested;
        // the moon glint is opposite the sun and fades in at night (uMoonFade), unshadowed by the sun's map.
        let sunH = normalize(V + params.uSunDirection);
        let sunSpec = pow(max(dot(N, sunH), 0.0), WaterSpecExp) * params.uSunFade * shadow * skyFac * WaterSpecGain;
        let moonH = normalize(V - params.uSunDirection);
        let moonSpec = pow(max(dot(N, moonH), 0.0), WaterSpecExp) * params.uMoonFade * skyFac * WaterSpecGain;

        var col = mix(baseColor, reflection, fresnel * skyFac);
        col = col + params.uSunColor * sunSpec + params.uMoonColor * moonSpec;
        r.color = vec4<f32>(ApplyFog(col, viewDepth), diffuse.a);
        return r;
    }

    r.color = vec4<f32>(ApplyFog(baseColor, viewDepth), diffuse.a);
    return r;
}

@fragment
fn fs_main(i: VsOut) -> @location(0) vec4<f32> {
    // Depth32float, read as-is (no depth*2-1 remap). The texel is addressed from uv — the SAME basis the
    // G-buffer colour/normal/light are sampled with — not from the fragment's framebuffer position, which runs
    // y-inverted relative to uv and would pair each pixel's colour with the vertically-mirrored pixel's depth.
    let depthDim = vec2<f32>(textureDimensions(uDepth));
    let depthRaw = textureLoad(uDepth, vec2<i32>(min(i.uv * depthDim, depthDim - 1.0)), 0);

    // Background = pixels the geometry pass never wrote, still at the reverse-Z far clear (depth == 0). Under the
    // infinite-far projection that point is literally at infinity (w -> 0), so its reconstruction is undefined;
    // sample the view ray at a finite depth instead (any depth along the pixel ray gives the same direction).
    if (depthRaw == 0.0) {
        // Reconstruct the view ray from two points off the inverse view-projection (reverse-Z: depth 1 = near,
        // 0.5 = further along the ray) and take their difference. This is robust where a single depth-0.5 point
        // minus the camera position is not — the inverse matrix is exact (geometry reconstructs from it), so the
        // ray direction is too, with no dependence on a separately-supplied camera position.
        let nearP = PositionFromDepth(i.uv, 1.0);
        let farP = PositionFromDepth(i.uv, 0.5);
        var sky = SkyColor(normalize(farP - nearP));
        if (params.uUnderwater > 0.5) { sky = ApplyUnderwater(sky, params.uSkyDistance); }
        return vec4<f32>(sky, 1.0);
    }

    let worldPos = PositionFromDepth(i.uv, depthRaw);
    let viewDepth = -(params.uView * vec4<f32>(worldPos, 1.0)).z;

    let res = GetColor(i.uv, worldPos, viewDepth);
    if (res.discard_) {
        discard;
    }
    if (params.uUnderwater > 0.5) {
        return vec4<f32>(ApplyUnderwater(res.color.rgb, viewDepth), res.color.a);
    }
    return res.color;
}

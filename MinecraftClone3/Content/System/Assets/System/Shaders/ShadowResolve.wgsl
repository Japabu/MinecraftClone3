// Half-resolution sun-shadow resolve. Ported from ShadowResolve.vs/.fs. A vertex-less fullscreen triangle
// (same as Tonemap.wgsl) runs a 12-tap rotated-Poisson PCF against the sun shadow map and writes a small
// shadow buffer the composition pass depth-aware-upsamples back to full res.
//   out.r = sun shadow factor (1 lit .. 0 shadowed)
//   out.g = normalized view depth (for the composition's bilateral upsample to reject taps across silhouettes)
//
// Reverse-Z notes (see the HARD CONTRACTS):
//   * The camera G-buffer depth (uDepth, Depth32float) is already WebGPU clip z in [0,1]; it is used AS-IS in
//     PositionFromDepth (NO GLSL `depth*2-1` remap). The ndc.xy is rebuilt from the fragment UV with the same
//     orientation as Tonemap.wgsl's fullscreen triangle.
//   * The shadow map is written by ShadowDepth.wgsl under a reverse-Z light view-projection (near=1, far=0).
//     The light projection the C# side supplies already yields clip z in [0,1], so the GLSL `p.z = lc.z/lc.w
//     * 0.5 + 0.5` becomes just `p.z = lc.z / lc.w` (no *0.5+0.5 on z). Only p.xy maps clip [-1,1] -> uv [0,1]
//     (and uv.y is flipped because clip-space y is up while texture v is down).
//   * The comparison sampler uses CompareFunction.Greater (GpuSamplers.ShadowCompare): a fragment is lit when
//     its light-space depth is GREATER than the stored occluder depth. The GLSL `refDepth = p.z - DepthBias`
//     (which biased toward the light under a LESS/[0,1] map) therefore becomes `refDepth = p.z + DepthBias`
//     (bias away from the light keeps the same peter-panning-vs-acne tradeoff under GREATER).
//   * The GLSL far-plane early-out `p.z > 1.0 -> return 1.0` (unshadowed) becomes the reverse-Z equivalent
//     `p.z <= 0.0 -> return 1.0` (far = 0 under reverse-Z).

// ----------------------------------------------------------------------------------------------------------
// group(0) binding(0): ShadowResolveParams uniform. std140-correct layout. C# mirror MUST match exactly.
//   field              wgsl type     size  offset (bytes)
//   uViewProjectionInv mat4x4<f32>   64    0
//   uView              mat4x4<f32>   64    64
//   uLightViewProj     mat4x4<f32>   64    128
//   uShadowTexel       f32           4     192
//   uShadowDistance    f32           4     196
//   uSunFade           f32           4     200
//   uShadowsEnabled    f32           4     204
//   uShadowSoftness    f32           4     208
//   uShadowMapTexel    f32           4     212
//   _pad0              f32           4     216   (padding so the struct end is 16-aligned)
//   _pad1              f32           4     220
//   total size: 224 bytes (16-aligned)
// ----------------------------------------------------------------------------------------------------------
struct ShadowResolveParams {
    uViewProjectionInv: mat4x4<f32>,
    uView: mat4x4<f32>,
    uLightViewProj: mat4x4<f32>,
    uShadowTexel: f32,
    uShadowDistance: f32,
    uSunFade: f32,
    uShadowsEnabled: f32,
    uShadowSoftness: f32,
    uShadowMapTexel: f32,
    _pad0: f32,
    _pad1: f32,
};
@group(0) @binding(0) var<uniform> params: ShadowResolveParams;

// group(1): the G-buffer / shadow-map textures + their samplers. Numbered sequentially:
//   binding(0) uNormal       Rgba8Unorm colour G-buffer  -> texture_2d<f32>      (encoded normal, .w material)
//   binding(1) uDepth        Depth32float camera depth    -> texture_depth_2d     (read via textureLoad)
//   binding(2) uLight        Rgba8Unorm colour G-buffer  -> texture_2d<f32>      (rgb block light, a sky factor)
//   binding(3) uShadowMap    Depth32float sun shadow map  -> texture_depth_2d     (sampled with comparison)
//   binding(4) gbufferSampler non-comparison sampler      -> sampler              (GpuSamplers.Framebuffer)
//   binding(5) shadowSampler  comparison sampler           -> sampler_comparison   (GpuSamplers.ShadowCompare, Greater)
@group(1) @binding(0) var uNormal: texture_2d<f32>;
@group(1) @binding(1) var uDepth: texture_depth_2d;
@group(1) @binding(2) var uLight: texture_2d<f32>;
@group(1) @binding(3) var uShadowMap: texture_depth_2d;
@group(1) @binding(4) var gbufferSampler: sampler;
@group(1) @binding(5) var shadowSampler: sampler_comparison;

const NormalBias: f32 = 2.0;
const DepthBias: f32 = 0.0005;

// 12-tap Poisson disc (unit radius). Rotated per pixel so the sparse taps read as smooth noise, not bands.
// var<private> (not const): it is indexed by a dynamic loop variable, which a module-scope const forbids.
var<private> Poisson: array<vec2<f32>, 12> = array<vec2<f32>, 12>(
    vec2<f32>(-0.326, -0.406), vec2<f32>(-0.840, -0.074), vec2<f32>(-0.696,  0.457), vec2<f32>(-0.203,  0.621),
    vec2<f32>( 0.962, -0.195), vec2<f32>( 0.473, -0.480), vec2<f32>( 0.519,  0.767), vec2<f32>( 0.185, -0.893),
    vec2<f32>( 0.507,  0.064), vec2<f32>( 0.896,  0.412), vec2<f32>(-0.322, -0.933), vec2<f32>(-0.792, -0.598));

struct VsOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) vTexCoord: vec2<f32>,
};

// Vertex-less fullscreen triangle — copied verbatim from Tonemap.wgsl so the image orientation matches.
@vertex
fn vs_main(@builtin(vertex_index) vid: u32) -> VsOut {
    var o: VsOut;
    let uv = vec2<f32>(f32((vid << 1u) & 2u), f32(vid & 2u));
    o.vTexCoord = uv;
    o.clip = vec4<f32>(uv * 2.0 - 1.0, 0.0, 1.0);
    return o;
}

fn DecodeNormal(normal: vec4<f32>) -> vec4<f32> {
    return normal * 2.0 - 1.0;
}

fn PositionFromDepth(vTexCoord: vec2<f32>, depth: f32) -> vec3<f32> {
    // ndc.y is negated vs uv (the uv basis used to sample the G-buffer runs y-down relative to clip-space y),
    // so a plain uv*2-1 reconstructs a vertically-mirrored world position. depth is reverse-Z clip z, AS-IS.
    let ndc = vec2<f32>(vTexCoord.x * 2.0 - 1.0, 1.0 - vTexCoord.y * 2.0);
    let clipSpace = vec4<f32>(ndc, depth, 1.0);
    let homogenousCoord = params.uViewProjectionInv * clipSpace;
    return homogenousCoord.xyz / homogenousCoord.w;
}

// 1.0 = fully sun-lit, 0.0 = fully shadowed. 12-tap rotated-Poisson hardware PCF.
fn SampleShadow(worldPos: vec3<f32>, normal: vec3<f32>, rot: mat2x2<f32>) -> f32 {
    let offsetPos = worldPos + normal * (params.uShadowTexel * NormalBias);
    let lc = params.uLightViewProj * vec4<f32>(offsetPos, 1.0);
    let ndc = lc.xyz / lc.w;
    // clip.xy [-1,1] -> uv [0,1], v flipped (clip y up, texture v down). z is already [0,1] (reverse-Z).
    let p = vec3<f32>(ndc.x * 0.5 + 0.5, ndc.y * -0.5 + 0.5, ndc.z);
    if (p.z <= 0.0) { return 1.0; }   // reverse-Z far plane (= GLSL `p.z > 1.0`)

    let refDepth = p.z + DepthBias;   // GREATER compare: bias away from the light (GLSL was `p.z - DepthBias`)
    var sum = 0.0;
    for (var i = 0; i < 12; i = i + 1) {
        let off = rot * Poisson[i] * (params.uShadowSoftness * params.uShadowMapTexel);
        sum = sum + textureSampleCompareLevel(uShadowMap, shadowSampler, p.xy + off, refDepth);
    }
    return sum / 12.0;
}

fn ShadowFactor(fragCoord: vec2<f32>, worldPos: vec3<f32>, normal: vec3<f32>, viewDepth: f32) -> f32 {
    // Per-pixel rotation of the Poisson disc (interleaved gradient noise) so the soft penumbra dithers
    // instead of banding. fragCoord = @builtin(position).xy is the WGSL analogue of gl_FragCoord.xy.
    let ign = fract(52.9829189 * fract(dot(fragCoord, vec2<f32>(0.06711056, 0.00583715))));
    let a = ign * 6.2831853;
    let rot = mat2x2<f32>(cos(a), -sin(a), sin(a), cos(a));

    let lit = SampleShadow(worldPos, normal, rot);

    // Fade the shadow out over the last 10% of the shadow distance so the far edge of coverage does not
    // pop from shadowed to lit.
    let fade = clamp((params.uShadowDistance - viewDepth) / (params.uShadowDistance * 0.1), 0.0, 1.0);
    return mix(1.0, lit, fade);
}

@fragment
fn fs_main(i: VsOut) -> @location(0) vec4<f32> {
    let normal = textureSampleLevel(uNormal, gbufferSampler, i.vTexCoord, 0.0);
    let lightSample = textureSampleLevel(uLight, gbufferSampler, i.vTexCoord, 0.0);

    var shadow = 1.0;
    var viewDepth = params.uShadowDistance;

    // Same early-outs as the GLSL inline path: skip the PCF where the directional sun term can't matter --
    // unlit geometry (normal.w == 1), night/dusk (uSunFade ~ 0), sky-occluded surfaces (lightSample.a ~ 0),
    // and past the shadow distance. Those pixels write shadow = 1.
    if (normal.w != 1.0 && params.uShadowsEnabled > 0.5 && params.uSunFade > 0.0 && lightSample.a > 0.004) {
        // Depth32float depth read via textureLoad (used AS-IS; reverse-Z clip z in [0,1]). The texel is
        // addressed from vTexCoord (this is a half-res pass, so the fragment's own framebuffer position does
        // NOT index the full-res camera depth) — the SAME uv basis the normal/light/worldPos use.
        let depthDim = vec2<f32>(textureDimensions(uDepth));
        let depth = textureLoad(uDepth, vec2<i32>(min(i.vTexCoord * depthDim, depthDim - 1.0)), 0);
        let worldPos = PositionFromDepth(i.vTexCoord, depth);
        viewDepth = -(params.uView * vec4<f32>(worldPos, 1.0)).z;
        if (viewDepth < params.uShadowDistance) {
            shadow = ShadowFactor(i.clip.xy, worldPos, DecodeNormal(normal).xyz, viewDepth);
        }
    }

    let normDepth = clamp(viewDepth / params.uShadowDistance, 0.0, 1.0);
    return vec4<f32>(shadow, normDepth, 0.0, 1.0);
}

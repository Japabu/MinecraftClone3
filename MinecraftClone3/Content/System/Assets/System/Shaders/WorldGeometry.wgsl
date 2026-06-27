// Deferred geometry pass: opaque + transparent chunk meshes into the 3-MRT G-buffer.
// Ported from WorldGeometry.vs/.fs. Positions are baked world-space at mesh time (no per-chunk model
// matrix), so every chunk shares one buffer set and draws with a single GPU-driven indirect multidraw.
//
// Binding convention (shared across world passes):
//   group(0) binding(0)  Frame uniform  (view, proj, cameraPos, ...)   — one write per frame
//   group(1) binding(0)  GeoParams uniform (LOD cross-fade + cutoff)    — per pass
//   group(2) binding(0..3) the four block-atlas texture arrays
//   group(2) binding(4)  the trilinear/aniso atlas sampler
//
// Vertex buffers (one per slot, matching the arena layout):
//   slot 0  position   vec3<f32>
//   slot 1  uv         vec2<f32>
//   slot 2  packed     u32   (texId<<0 | arrayId<<16 | normalIndex<<18 | material<<21)
//   slot 3  color      unorm8x4 -> vec4<f32>  (tint; a unused)
//   slot 4  light      unorm8x4 -> vec4<f32>  (rgb block light, a sky factor)

struct Frame {
    view: mat4x4<f32>,
    proj: mat4x4<f32>,
    cameraPos: vec3<f32>,
    _pad0: f32,
};
@group(0) @binding(0) var<uniform> frame: Frame;

struct GeoParams {
    fadeStart: f32,   // LOD band inner edge (RenderDistance - width); >= fadeEnd disables the fade
    fadeEnd: f32,     // LOD band outer edge (= RenderDistance)
    fadeMode: u32,    // 0 = near chunks fade out, 1 = horizon LOD fades in
    cutoff: u32,      // 1 = anti-aliased alpha test (cutout foliage)
};
@group(1) @binding(0) var<uniform> geo: GeoParams;

@group(2) @binding(0) var tex16: texture_2d_array<f32>;
@group(2) @binding(1) var tex64: texture_2d_array<f32>;
@group(2) @binding(2) var tex256: texture_2d_array<f32>;
@group(2) @binding(3) var tex1024: texture_2d_array<f32>;
@group(2) @binding(4) var atlasSampler: sampler;        // nearest-mag, trilinear-min: crisp up close
@group(2) @binding(5) var atlasSamplerAniso: sampler;   // all-linear + 16x anisotropy: sharp when minified

// var<private> (not const): WGSL only allows a *constant* index into a module-scope const array, and these
// are indexed dynamically (by the packed normal/material bits and the screen-space Bayer cell).
var<private> Normals: array<vec3<f32>, 6> = array<vec3<f32>, 6>(
    vec3<f32>( 1.0, 0.0, 0.0), vec3<f32>(-1.0, 0.0, 0.0),
    vec3<f32>( 0.0, 1.0, 0.0), vec3<f32>( 0.0,-1.0, 0.0),
    vec3<f32>( 0.0, 0.0, 1.0), vec3<f32>( 0.0, 0.0,-1.0));
// Material -> normal.w flag (0 lit, 0.5 water, 1 unlit), kept so Composition decoding is unchanged.
var<private> Material: array<f32, 3> = array<f32, 3>(0.0, 0.5, 1.0);

var<private> Bayer4: array<f32, 16> = array<f32, 16>(
     0.0/16.0,  8.0/16.0,  2.0/16.0, 10.0/16.0,
    12.0/16.0,  4.0/16.0, 14.0/16.0,  6.0/16.0,
     3.0/16.0, 11.0/16.0,  1.0/16.0,  9.0/16.0,
    15.0/16.0,  7.0/16.0, 13.0/16.0,  5.0/16.0);

struct VsOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) texCoord: vec4<f32>,   // xy uv, z texId (-1 = none), w arrayId (-1 = none)
    @location(1) normal: vec4<f32>,     // xyz axis normal, w material flag
    @location(2) color: vec3<f32>,
    @location(3) light: vec4<f32>,
    @location(4) worldPos: vec3<f32>,
};

@vertex
fn vs_main(
    @location(0) inPosition: vec3<f32>,
    @location(1) inUv: vec2<f32>,
    @location(2) inPacked: u32,
    @location(3) inColor: vec4<f32>,
    @location(4) inLight: vec4<f32>,
) -> VsOut {
    var o: VsOut;
    o.clip = frame.proj * frame.view * vec4<f32>(inPosition, 1.0);
    o.worldPos = inPosition;

    let texId = inPacked & 0xFFFFu;
    let arrayId = (inPacked >> 16u) & 0x3u;
    let normalIndex = (inPacked >> 18u) & 0x7u;
    let material = (inPacked >> 21u) & 0x3u;

    let noTex = (texId == 0xFFFFu);
    let tex = select(f32(texId), -1.0, noTex);
    let arr = select(f32(arrayId), -1.0, noTex);
    o.texCoord = vec4<f32>(inUv, tex, arr);
    o.normal = vec4<f32>(Normals[normalIndex], Material[material]);
    o.color = inColor.rgb;
    o.light = inLight;
    return o;
}

struct GBuffer {
    @location(0) diffuse: vec4<f32>,
    @location(1) normal: vec4<f32>,
    @location(2) light: vec4<f32>,
};

fn lodDiscard(worldPos: vec3<f32>, fragCoord: vec2<f32>) -> bool {
    let fade = clamp((distance(worldPos, frame.cameraPos) - geo.fadeStart)
        / max(geo.fadeEnd - geo.fadeStart, 0.001), 0.0, 1.0);
    let p = vec2<i32>(fragCoord) & vec2<i32>(3, 3);
    let dither = Bayer4[p.y * 4 + p.x];
    if (geo.fadeMode == 0u) { return dither < fade; }   // chunk: gone where dither below fade
    return dither >= fade;                               // LOD: shows where the chunk is gone
}

fn sampleAtlas(texCoord: vec4<f32>, s: sampler, ddx: vec2<f32>, ddy: vec2<f32>) -> vec4<f32> {
    let uv = texCoord.xy;
    let layer = i32(texCoord.z);
    let arr = i32(texCoord.w);
    // textureSampleGrad takes explicit gradients, so it is legal in the non-uniform array selection below
    // (plain textureSample, which needs implicit derivatives, is not). Derivatives are hoisted by the caller.
    if (arr == 0) { return textureSampleGrad(tex16, s, uv, layer, ddx, ddy); }
    if (arr == 1) { return textureSampleGrad(tex64, s, uv, layer, ddx, ddy); }
    if (arr == 2) { return textureSampleGrad(tex256, s, uv, layer, ddx, ddy); }
    if (arr == 3) { return textureSampleGrad(tex1024, s, uv, layer, ddx, ddy); }
    return vec4<f32>(0.0);
}

@fragment
fn fs_main(i: VsOut) -> GBuffer {
    if (lodDiscard(i.worldPos, i.clip.xy)) { discard; }

    var diffuse: vec4<f32>;
    if (i.normal.w == 1.0) {
        // Unlit solid (e.g. wireframe boxes): tint straight through, no texture.
        diffuse = vec4<f32>(i.color, 1.0);
    } else {
        let ddx = dpdx(i.texCoord.xy);
        let ddy = dpdy(i.texCoord.xy);

        // Pick the sampler by minification. WebGPU forbids one sampler mixing nearest magnification with
        // anisotropy, but anisotropy is a no-op under magnification, so split: crisp nearest-mag up close, and
        // a 16x-anisotropic linear sampler once the texel footprint exceeds a pixel (distant/grazing). Blend
        // across the boundary so the switch isn't itself a seam. Array layer size = 16 * 4^arrayId.
        let arrSize = 16.0 * pow(4.0, i.texCoord.w);
        let footprint = max(length(ddx), length(ddy)) * arrSize;
        let anisoMix = clamp(footprint - 1.0, 0.0, 1.0);
        var texColor: vec4<f32>;
        if (anisoMix <= 0.0) {
            texColor = sampleAtlas(i.texCoord, atlasSampler, ddx, ddy);
        } else if (anisoMix >= 1.0) {
            texColor = sampleAtlas(i.texCoord, atlasSamplerAniso, ddx, ddy);
        } else {
            texColor = mix(sampleAtlas(i.texCoord, atlasSampler, ddx, ddy),
                           sampleAtlas(i.texCoord, atlasSamplerAniso, ddx, ddy), anisoMix);
        }
        texColor = vec4<f32>(texColor.rgb * i.color, texColor.a);

        if (geo.cutoff != 0u) {
            // Anti-aliased alpha test: sharpen the sampled alpha by its screen-space gradient so cutout foliage
            // keeps a ~1px edge at every mip instead of dissolving (the median-preserving alpha test). With the
            // anisotropic sampler keeping the minified mip sharp, leaf coverage holds at distance.
            let a = (texColor.a - 0.5) / max(fwidth(texColor.a), 0.0001) + 0.5;
            if (a < 0.5) { discard; }
        }
        diffuse = texColor;
    }

    var o: GBuffer;
    o.diffuse = diffuse;
    o.normal = i.normal * 0.5 + 0.5;   // EncodeNormal
    o.light = i.light;
    return o;
}

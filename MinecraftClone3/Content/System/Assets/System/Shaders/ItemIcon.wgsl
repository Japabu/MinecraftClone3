// Renders a single block's chunk-format mesh into an off-screen inventory icon. Same packed vertex layout as
// WorldGeometry, but forward-shaded with a fixed per-face brightness (no G-buffer, no world light) for the
// Minecraft isometric look. Ported from ItemIcon.vs/.fs.

struct IconFrame {
    view: mat4x4<f32>,
    proj: mat4x4<f32>,
};
@group(0) @binding(0) var<uniform> frame: IconFrame;

@group(1) @binding(0) var tex16: texture_2d_array<f32>;
@group(1) @binding(1) var tex64: texture_2d_array<f32>;
@group(1) @binding(2) var tex256: texture_2d_array<f32>;
@group(1) @binding(3) var tex1024: texture_2d_array<f32>;
@group(1) @binding(4) var atlasSampler: sampler;

// Fixed face shading by packed normalIndex (+X,-X,+Y,-Y,+Z,-Z): top brightest, bottom darkest.
var<private> Shade: array<f32, 6> = array<f32, 6>(0.6, 0.6, 1.0, 0.5, 0.8, 0.8);

struct VsOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) texCoord: vec3<f32>,   // xy uv, z texId (layer)
    @location(1) @interpolate(flat) arrayId: i32,
    @location(2) color: vec3<f32>,
    @location(3) shade: f32,
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

    let texId = inPacked & 0xFFFFu;
    let arrayId = (inPacked >> 16u) & 0x3u;
    let normalIndex = (inPacked >> 18u) & 0x7u;

    let noTex = (texId == 0xFFFFu);
    o.texCoord = vec3<f32>(inUv, select(f32(texId), -1.0, noTex));
    o.arrayId = select(i32(arrayId), -1, noTex);
    o.color = inColor.rgb;
    o.shade = Shade[normalIndex];
    return o;
}

fn sampleAtlas(arrayId: i32, coord: vec3<f32>, ddx: vec2<f32>, ddy: vec2<f32>) -> vec4<f32> {
    let uv = coord.xy;
    let layer = i32(coord.z);
    if (arrayId == 0) { return textureSampleGrad(tex16, atlasSampler, uv, layer, ddx, ddy); }
    if (arrayId == 1) { return textureSampleGrad(tex64, atlasSampler, uv, layer, ddx, ddy); }
    if (arrayId == 2) { return textureSampleGrad(tex256, atlasSampler, uv, layer, ddx, ddy); }
    if (arrayId == 3) { return textureSampleGrad(tex1024, atlasSampler, uv, layer, ddx, ddy); }
    return vec4<f32>(0.0);
}

@fragment
fn fs_main(i: VsOut) -> @location(0) vec4<f32> {
    let ddx = dpdx(i.texCoord.xy);
    let ddy = dpdy(i.texCoord.xy);
    var c = vec4<f32>(i.color, 1.0);
    if (i.arrayId >= 0) {
        c = sampleAtlas(i.arrayId, i.texCoord, ddx, ddy);
    }
    // Drop fully transparent texels so the icon keeps the framebuffer's transparent background.
    if (c.a < 0.1) { discard; }
    c = vec4<f32>(c.rgb * i.color * i.shade, c.a);
    return c;
}

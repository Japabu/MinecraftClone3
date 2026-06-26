// Entity geometry into the G-buffer. Ported from EntityGeometry.vs/.fs. Unlike chunks (baked world-space),
// an entity is a small rigid model drawn with a per-entity (and per-animated-part) model matrix supplied via
// a dynamic-offset uniform, so the vertex shader applies `model` here. One flat block+sky light for the whole
// entity (sampled at its position CPU-side) so it darkens in caves and brightens in the open.

struct Frame {
    view: mat4x4<f32>,
    proj: mat4x4<f32>,
    cameraPos: vec3<f32>,
    _pad0: f32,
};
@group(0) @binding(0) var<uniform> frame: Frame;

struct EntityDraw {
    model: mat4x4<f32>,
    light: vec4<f32>,   // flat block+sky light for the whole entity
};
@group(1) @binding(0) var<uniform> entity: EntityDraw;

@group(2) @binding(0) var tex16: texture_2d_array<f32>;
@group(2) @binding(1) var tex64: texture_2d_array<f32>;
@group(2) @binding(2) var tex256: texture_2d_array<f32>;
@group(2) @binding(3) var tex1024: texture_2d_array<f32>;
@group(2) @binding(4) var atlasSampler: sampler;

var<private> Normals: array<vec3<f32>, 6> = array<vec3<f32>, 6>(
    vec3<f32>( 1.0, 0.0, 0.0), vec3<f32>(-1.0, 0.0, 0.0),
    vec3<f32>( 0.0, 1.0, 0.0), vec3<f32>( 0.0,-1.0, 0.0),
    vec3<f32>( 0.0, 0.0, 1.0), vec3<f32>( 0.0, 0.0,-1.0));

struct VsOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) texCoord: vec4<f32>,   // xy uv, z texId, w arrayId (-1 = none)
    @location(1) normal: vec4<f32>,
    @location(2) color: vec3<f32>,
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
    o.clip = frame.proj * frame.view * entity.model * vec4<f32>(inPosition, 1.0);

    let texId = inPacked & 0xFFFFu;
    let arrayId = (inPacked >> 16u) & 0x3u;
    let normalIndex = (inPacked >> 18u) & 0x7u;

    let noTex = (texId == 0xFFFFu);
    o.texCoord = vec4<f32>(inUv, select(f32(texId), -1.0, noTex), select(f32(arrayId), -1.0, noTex));

    // Rotate the normal by the model matrix (direction, w=0) so it stays correct under yaw/animation.
    let n = normalize((entity.model * vec4<f32>(Normals[normalIndex], 0.0)).xyz);
    o.normal = vec4<f32>(n, 0.0);   // material .w = 0 => lit
    o.color = inColor.rgb;
    return o;
}

struct GBuffer {
    @location(0) diffuse: vec4<f32>,
    @location(1) normal: vec4<f32>,
    @location(2) light: vec4<f32>,
};

fn sampleAtlas(texCoord: vec4<f32>, ddx: vec2<f32>, ddy: vec2<f32>, fallback: vec3<f32>) -> vec4<f32> {
    let uv = texCoord.xy;
    let layer = i32(texCoord.z);
    let arr = i32(texCoord.w);
    if (arr == 0) { return textureSampleGrad(tex16, atlasSampler, uv, layer, ddx, ddy); }
    if (arr == 1) { return textureSampleGrad(tex64, atlasSampler, uv, layer, ddx, ddy); }
    if (arr == 2) { return textureSampleGrad(tex256, atlasSampler, uv, layer, ddx, ddy); }
    if (arr == 3) { return textureSampleGrad(tex1024, atlasSampler, uv, layer, ddx, ddy); }
    return vec4<f32>(fallback, 1.0);
}

@fragment
fn fs_main(i: VsOut) -> GBuffer {
    let ddx = dpdx(i.texCoord.xy);
    let ddy = dpdy(i.texCoord.xy);
    let texColor = sampleAtlas(i.texCoord, ddx, ddy, i.color);
    // Entity sheets have fully-transparent regions (box-unwrap gaps, capes); drop them.
    if (texColor.a < 0.5) { discard; }

    var o: GBuffer;
    o.diffuse = vec4<f32>(texColor.rgb * i.color, 1.0);
    o.normal = i.normal * 0.5 + 0.5;
    o.light = entity.light;
    return o;
}

// 2D sprite / GUI quad. Per-draw rect/uv/colour come in as push constants
// (cheap per-sprite updates); the texture + sampler are a bind group. Draws a unit quad (NDC corners in
// the vertex buffer, slot 0) remapped to the target rect.

struct SpritePush {
    rect: vec4<f32>,    // xy = min, zw = max, in 0..1 GUI space
    uvRect: vec4<f32>,  // xy = min, zw = max texture coords
    color: vec4<f32>,
};
var<push_constant> pc: SpritePush;

@group(0) @binding(0) var spriteTex: texture_2d<f32>;
@group(0) @binding(1) var spriteSampler: sampler;

struct VsOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@location(0) inPosition: vec3<f32>) -> VsOut {
    var o: VsOut;
    let n = inPosition.xy * 0.5 + 0.5;
    let xy = (pc.rect.xy + n * (pc.rect.zw - pc.rect.xy)) * vec2<f32>(1.0, -1.0);
    o.clip = vec4<f32>(xy, inPosition.z, 1.0);
    o.uv = pc.uvRect.xy + n * (pc.uvRect.zw - pc.uvRect.xy);
    return o;
}

@fragment
fn fs_main(i: VsOut) -> @location(0) vec4<f32> {
    return textureSample(spriteTex, spriteSampler, i.uv) * pc.color;
}

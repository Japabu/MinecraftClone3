// Progressive block-breaking crack overlay. The textured cube is drawn over the mined block and blended
// into the G-buffer diffuse only (the normal + light attachments are masked by the pipeline), so the block
// keeps its own shading and composition lights the cracked surface as one. Per-draw transform is a push
// constant; the stage texture rides group 0.

struct BreakPush {
    transform: mat4x4<f32>,
};
var<push_constant> pc: BreakPush;

struct VsOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@location(0) position: vec3<f32>, @location(1) uv: vec2<f32>) -> VsOut {
    var o: VsOut;
    o.clip = pc.transform * vec4<f32>(position, 1.0);
    o.uv = uv;
    return o;
}

@group(0) @binding(0) var crackTex: texture_2d<f32>;
@group(0) @binding(1) var crackSampler: sampler;

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let c = textureSample(crackTex, crackSampler, in.uv);
    if (c.a < 0.01) { discard; }
    return c;
}

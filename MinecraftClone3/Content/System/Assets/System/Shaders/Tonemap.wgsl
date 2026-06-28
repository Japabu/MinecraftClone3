// Final present pass: the HDR scene (rgba16float, written by Composition) → the surface (LDR UNORM). A
// vertex-less fullscreen triangle samples the scene colour and clamps it to [0,1]. The lighting is composited
// in display (gamma) space, so no tonemap curve or gamma re-encode is applied here.

@group(0) @binding(0) var hdrTex: texture_2d<f32>;
@group(0) @binding(1) var hdrSampler: sampler;

struct VsOut {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) vid: u32) -> VsOut {
    // Oversized triangle covering the viewport: uv 0..2, clip -1..3.
    var o: VsOut;
    let uv = vec2<f32>(f32((vid << 1u) & 2u), f32(vid & 2u));
    o.uv = uv;
    o.clip = vec4<f32>(uv * 2.0 - 1.0, 0.0, 1.0);
    return o;
}

@fragment
fn fs_main(i: VsOut) -> @location(0) vec4<f32> {
    // The lighting is composited directly in display space, so the present pass just clamps to [0,1] — no
    // tonemap curve and no gamma re-encode (both would shift the look away from the composited colour).
    let hdr = textureSample(hdrTex, hdrSampler, i.uv).rgb;
    return vec4<f32>(clamp(hdr, vec3<f32>(0.0), vec3<f32>(1.0)), 1.0);
}

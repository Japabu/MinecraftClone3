// Final present pass: the HDR scene (rgba16float, written by Composition) → the surface (LDR UNORM). A
// vertex-less fullscreen triangle samples the scene colour and clamps it to [0,1]. The lighting is composited
// in display (gamma) space, so no tonemap curve or gamma re-encode is applied here.

@group(0) @binding(0) var hdrTex: texture_2d<f32>;
@group(0) @binding(1) var hdrSampler: sampler;

// Nether-portal screen warp (Minecraft's nausea wobble): intensity 0..1 = how deep into the portal soak the
// player is, time = a free-running clock for the wobble phase. intensity 0 leaves the sample uv untouched.
struct Warp {
    intensity: f32,
    time: f32,
};
var<push_constant> pc: Warp;

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
    // Two crossed sine waves displace the sample uv, amplitude scaling with the portal intensity, for the
    // queasy screen wobble vanilla shows in a portal. pc.intensity is a push constant (uniform), so this stays
    // uniform control flow and edge samples clamp (the present sampler is clamp-to-edge). intensity 0 = no shift.
    var uv = i.uv;
    let amp = pc.intensity * 0.018;
    uv.x = uv.x + sin(uv.y * 14.0 + pc.time * 3.1) * amp;
    uv.y = uv.y + cos(uv.x * 12.0 + pc.time * 2.7) * amp;

    // The lighting is composited directly in display space, so the present pass just clamps to [0,1] — no
    // tonemap curve and no gamma re-encode (both would shift the look away from the composited colour).
    let hdr = textureSample(hdrTex, hdrSampler, uv).rgb;
    return vec4<f32>(clamp(hdr, vec3<f32>(0.0), vec3<f32>(1.0)), 1.0);
}

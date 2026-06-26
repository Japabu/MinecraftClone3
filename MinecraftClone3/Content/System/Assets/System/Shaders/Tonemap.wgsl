// Final tonemap pass: the HDR scene (rgba16float, written by Composition) → the surface (LDR UNORM).
// A vertex-less fullscreen triangle samples the HDR colour, applies ACES filmic tonemapping, then encodes
// gamma (the surface is a non-sRGB UNORM format — see GpuContext.ConfigurePreferredFormat — so the shader
// owns the gamma curve rather than letting an sRGB swapchain double-encode).

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

// Narkowicz ACES filmic approximation.
fn aces(x: vec3<f32>) -> vec3<f32> {
    let a = 2.51;
    let b = 0.03;
    let c = 2.43;
    let d = 0.59;
    let e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), vec3<f32>(0.0), vec3<f32>(1.0));
}

@fragment
fn fs_main(i: VsOut) -> @location(0) vec4<f32> {
    let hdr = textureSample(hdrTex, hdrSampler, i.uv).rgb;
    let mapped = aces(hdr);
    let gamma = pow(mapped, vec3<f32>(1.0 / 2.2));
    return vec4<f32>(gamma, 1.0);
}

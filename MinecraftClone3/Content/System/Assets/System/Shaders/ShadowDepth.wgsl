// Shadow cascade depth-only pass. Chunk positions are baked world-space
// (same vertex buffer as the geometry pass, slot 0), transformed by the reverse-Z light view-projection.
// No fragment output — the pipeline writes depth only.

struct ShadowFrame {
    lightViewProj: mat4x4<f32>,
};
@group(0) @binding(0) var<uniform> shadow: ShadowFrame;

@vertex
fn vs_main(@location(0) inPosition: vec3<f32>) -> @builtin(position) vec4<f32> {
    return shadow.lightViewProj * vec4<f32>(inPosition, 1.0);
}

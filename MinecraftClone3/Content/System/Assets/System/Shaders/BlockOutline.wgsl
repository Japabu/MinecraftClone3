// Block-selection wireframe box. Drawn into the G-buffer as an unlit
// overlay (diffuse = colour, normal.w = 1 so Composition skips shading). Per-draw transform + colour are
// push constants.

struct OutlinePush {
    transform: mat4x4<f32>,
    color: vec4<f32>,
};
var<push_constant> pc: OutlinePush;

@vertex
fn vs_main(@location(0) inPosition: vec3<f32>) -> @builtin(position) vec4<f32> {
    return pc.transform * vec4<f32>(inPosition, 1.0);
}

struct GBufferOverlay {
    @location(0) diffuse: vec4<f32>,
    @location(1) normal: vec4<f32>,
};

@fragment
fn fs_main() -> GBufferOverlay {
    var o: GBufferOverlay;
    o.diffuse = pc.color;
    o.normal = vec4<f32>(0.0, 0.0, 0.0, 1.0);   // w=1 -> unlit in Composition
    return o;
}

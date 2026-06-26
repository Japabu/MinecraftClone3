// Compute mipmap generation for the block-atlas texture arrays. WebGPU has no GenerateMipmap, so each
// level is produced from the one above by an exact 2x2 box downsample. Dispatched once per (level -> level+1)
// transition, covering all array layers in the z dimension. textureLoad reads the exact 2x2 footprint (no
// sampler, no implicit filtering), so the result is a crisp, correct box filter — alpha included, which the
// foliage anti-aliased alpha test then compensates for at sample time.

@group(0) @binding(0) var srcMip: texture_2d_array<f32>;
@group(0) @binding(1) var dstMip: texture_storage_2d_array<rgba8unorm, write>;

@compute @workgroup_size(8, 8, 1)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let dstSize = textureDimensions(dstMip);
    if (gid.x >= dstSize.x || gid.y >= dstSize.y) {
        return;
    }
    let layer = i32(gid.z);
    let c = vec2<i32>(i32(gid.x) * 2, i32(gid.y) * 2);
    let s0 = textureLoad(srcMip, c + vec2<i32>(0, 0), layer, 0);
    let s1 = textureLoad(srcMip, c + vec2<i32>(1, 0), layer, 0);
    let s2 = textureLoad(srcMip, c + vec2<i32>(0, 1), layer, 0);
    let s3 = textureLoad(srcMip, c + vec2<i32>(1, 1), layer, 0);
    let avg = (s0 + s1 + s2 + s3) * 0.25;
    textureStore(dstMip, vec2<i32>(i32(gid.x), i32(gid.y)), layer, avg);
}

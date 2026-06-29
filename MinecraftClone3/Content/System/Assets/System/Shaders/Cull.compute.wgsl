// GPU-driven chunk culling. One invocation per resident chunk slot: frustum-test the chunk's 16^3 AABB and
// emit a DrawIndexedIndirect command for the visible ones. Two output modes, picked by params.compact:
//   compact == 1  -> atomically APPEND visible chunks into a packed list + count; the pass draws it with one
//                    MultiDrawIndexedIndirectCount (Vulkan/D3D12, where that wgpu-native extension exists).
//   compact == 0  -> write one command PER SLOT (zero = no-op) so the pass can loop plain DrawIndexedIndirect
//                    with a CPU-known slot count, since Metal has no MultiDrawIndirectCount.
// Either way the CPU never builds the visible set.
//
// Reused for the opaque, shadow-depth, and LOD passes by swapping the frustum planes (camera frustum vs the
// light-space frustum) and the metadata/draw buffers. Positions are baked world-space, so a chunk's AABB is
// just [minCorner, minCorner + 16].

struct ChunkMeta {
    minCorner: vec3<f32>,
    indexCount: u32,
    firstIndex: u32,
    baseVertex: i32,
    flags: u32,
    _pad: u32,
};

// Matches WebGPU's DrawIndexedIndirect layout (5 x u32): indexCount, instanceCount, firstIndex, baseVertex, firstInstance.
struct DrawCmd {
    indexCount: u32,
    instanceCount: u32,
    firstIndex: u32,
    baseVertex: i32,
    firstInstance: u32,
};

struct CullParams {
    planes: array<vec4<f32>, 6>,   // frustum planes, xyz = normal, w = offset (inside = dot(n,p)+w >= 0)
    cameraPos: vec3<f32>,
    chunkExtent: f32,              // 16
    chunkCount: u32,
    maxDraws: u32,
    maxDistance: f32,              // chunk-centre distance cull (the arena caches past the render distance); <= 0 disables
    compact: u32,                  // 1 = packed append + count (MultiDrawIndirectCount); 0 = per-slot no-op commands
};

@group(0) @binding(0) var<uniform> params: CullParams;
@group(0) @binding(1) var<storage, read> metas: array<ChunkMeta>;
@group(0) @binding(2) var<storage, read_write> draws: array<DrawCmd>;
@group(0) @binding(3) var<storage, read_write> drawCount: atomic<u32>;

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if (i >= params.chunkCount) {
        return;
    }
    let m = metas[i];

    let mn = m.minCorner;
    let mx = mn + vec3<f32>(params.chunkExtent, params.chunkExtent, params.chunkExtent);
    let center = mn + vec3<f32>(params.chunkExtent * 0.5);

    var visible = m.indexCount != 0u;   // freed slots keep indexCount 0 so they never draw

    // Distance cull: the client caches chunks past the render distance, so drop those whose centre is beyond
    // it before the frustum test. maxDistance <= 0 disables it.
    if (visible && params.maxDistance > 0.0 && distance(center, params.cameraPos) > params.maxDistance) {
        visible = false;
    }

    // Conservative AABB-vs-frustum: a chunk is culled only if it lies fully outside one plane, tested via
    // the box's positive vertex (the corner farthest along the plane normal).
    if (visible) {
        for (var p = 0u; p < 6u; p = p + 1u) {
            let plane = params.planes[p];
            let pv = vec3<f32>(
                select(mn.x, mx.x, plane.x >= 0.0),
                select(mn.y, mx.y, plane.y >= 0.0),
                select(mn.z, mx.z, plane.z >= 0.0));
            if (dot(plane.xyz, pv) + plane.w < 0.0) {
                visible = false;
                break;
            }
        }
    }

    if (params.compact == 1u) {
        if (visible) {
            let slot = atomicAdd(&drawCount, 1u);
            if (slot < params.maxDraws) {
                draws[slot] = DrawCmd(m.indexCount, 1u, m.firstIndex, m.baseVertex, 0u);
            }
        }
    } else {
        // Per-slot: every resident slot writes its own command so the draw side can issue one
        // DrawIndexedIndirect per slot with no GPU-side count. A culled/empty slot is a zero (no-op) command.
        if (visible) {
            draws[i] = DrawCmd(m.indexCount, 1u, m.firstIndex, m.baseVertex, 0u);
        } else {
            draws[i] = DrawCmd(0u, 0u, 0u, 0, 0u);
        }
    }
}

using System;
using System.Runtime.InteropServices;
using MinecraftClone3API.Graphics.Rhi;
using MinecraftClone3API.Util;
using Silk.NET.Maths;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// GPU-driven chunk culling for one render pass. Each frame it uploads the pass's frustum planes + camera
    /// distance, dispatches the <c>Cull</c> compute shader over the arena's <see cref="ChunkMeta"/> buffer, and
    /// the shader appends a <c>DrawIndexedIndirect</c> command per visible chunk into <see cref="Indirect"/>
    /// (counting into <see cref="Count"/>). The geometry/shadow pass then issues one
    /// <see cref="RenderPass.MultiDrawIndexedIndirectCount"/> — the CPU never builds a visible set. One instance
    /// per pass (camera-opaque, sun-shadow, LOD horizon) so each keeps its own frustum + indirect list.
    /// </summary>
    public sealed class ChunkCuller : IDisposable
    {
        // Matches CullParams in Cull.compute.wgsl (128 bytes): 6 planes (96) + cameraPos+chunkExtent (16) +
        // chunkCount/maxDraws/maxDistance/compact (16).
        [StructLayout(LayoutKind.Sequential)]
        private struct CullParams
        {
            public Vector4 P0, P1, P2, P3, P4, P5;
            public Vector3 CameraPos;
            public float ChunkExtent;
            public uint ChunkCount;
            public uint MaxDraws;
            public float MaxDistance;
            public uint Compact;
        }

        // True only where wgpu exposes MultiDrawIndirectCount (Vulkan/D3D12): then the cull packs the visible
        // set + a count and one multidraw issues it. On Metal it's false, so the cull writes a command per slot
        // and the pass loops plain DrawIndexedIndirect over the resident slots (zero commands are no-ops).
        private static bool Compacted => Gpu.Features.MultiDrawIndirectCount;

        // WebGPU DrawIndexedIndirect command = 5 x u32.
        private const int IndirectStride = 20;

        // Matches the 128-byte CullParams WGSL uniform (see the struct above).
        private const int ParamsBytes = 128;

        private static GpuBindGroupLayout _layout;
        private static GpuPipelineLayout _pipeLayout;
        private static GpuComputePipeline _pipeline;

        /// <summary>Build the shared cull pipeline + bind-group layout once (after the device exists).</summary>
        public static void Load(Func<string, string> readShader)
        {
            if (_pipeline != null) return;
            _layout = new GpuBindGroupLayout(new[]
            {
                GpuBindGroupLayout.Buffer(0, ShaderStage.Compute, BufferBindingType.Uniform),
                GpuBindGroupLayout.Buffer(1, ShaderStage.Compute, BufferBindingType.ReadOnlyStorage),
                GpuBindGroupLayout.Buffer(2, ShaderStage.Compute, BufferBindingType.Storage),
                GpuBindGroupLayout.Buffer(3, ShaderStage.Compute, BufferBindingType.Storage),
            }, "cull");
            _pipeLayout = new GpuPipelineLayout(new[] { GpuPipelineLayout.Ptr(_layout) }, label: "cull");
            var module = new GpuShaderModule(readShader("System/Shaders/Cull.compute.wgsl"), "cull");
            _pipeline = new GpuComputePipeline(_pipeLayout, module, "main", "cull");
            module.Dispose();
        }

        private readonly string _label;
        private readonly GpuBuffer _params;
        private readonly GpuBuffer _count;
        private GpuBuffer _indirect;
        private int _maxDraws;

        private GpuBindGroup _bind;
        private GpuBuffer _boundMeta;
        private GpuBuffer _boundIndirect;

        // Resident slots dispatched this frame; the per-slot draw loop issues this many DrawIndexedIndirect.
        private int _drawSlots;

        public ChunkCuller(string label, int initialDraws = 4096)
        {
            _label = label;
            _maxDraws = initialDraws;
            _params = new GpuBuffer(ParamsBytes,
                BufferUsage.Uniform | BufferUsage.CopyDst, $"{label}.cullParams");
            _indirect = new GpuBuffer((ulong) (_maxDraws * IndirectStride),
                BufferUsage.Storage | BufferUsage.Indirect, $"{label}.indirect");
            _count = new GpuBuffer(sizeof(uint),
                BufferUsage.Storage | BufferUsage.Indirect | BufferUsage.CopyDst, $"{label}.count");
        }

        /// <summary>Record the cull compute for this pass into <paramref name="encoder"/>: reset the count,
        /// upload the frustum + distance params, and dispatch one invocation per resident chunk slot.
        /// <paramref name="maxDistance"/> &lt;= 0 disables the distance cull (e.g. the sun-shadow pass, which
        /// is bounded by its own light frustum). <paramref name="chunkExtent"/> is the cube side of each
        /// entry's AABB (16 for chunks; the LOD pass passes its region's full extent).</summary>
        public void Dispatch(GpuCommandEncoder encoder, ChunkMeshArena arena, Frustum frustum,
            Vector3 cameraPos, float maxDistance, float chunkExtent)
        {
            // Always reset the count so a frame that culls everything (or has no chunks) draws nothing.
            _count.QueueWriteStruct(0u);
            _drawSlots = 0;

            var count = arena.MetaCount;
            if (count == 0) return;

            if (count > _maxDraws)
            {
                while (count > _maxDraws) _maxDraws *= 2;
                _indirect.Dispose();
                _indirect = new GpuBuffer((ulong) (_maxDraws * IndirectStride),
                    BufferUsage.Storage | BufferUsage.Indirect, $"{_label}.indirect");
            }

            if (_bind == null || _boundMeta != arena.MetaBuffer || _boundIndirect != _indirect)
            {
                _bind?.Dispose();
                _bind = new GpuBindGroup(_layout, new[]
                {
                    GpuBindGroup.Buffer(0, _params),
                    GpuBindGroup.Buffer(1, arena.MetaBuffer),
                    GpuBindGroup.Buffer(2, _indirect),
                    GpuBindGroup.Buffer(3, _count),
                }, $"{_label}.cull");
                _boundMeta = arena.MetaBuffer;
                _boundIndirect = _indirect;
            }

            var p = new CullParams
            {
                P0 = Plane(frustum, 0), P1 = Plane(frustum, 1), P2 = Plane(frustum, 2),
                P3 = Plane(frustum, 3), P4 = Plane(frustum, 4), P5 = Plane(frustum, 5),
                CameraPos = cameraPos,
                ChunkExtent = chunkExtent,
                ChunkCount = (uint) count,
                MaxDraws = (uint) _maxDraws,
                MaxDistance = maxDistance,
                Compact = Compacted ? 1u : 0u,
            };
            _params.QueueWriteStruct(p);

            var pass = ComputePass.Begin(encoder, _label);
            pass.SetPipeline(_pipeline);
            pass.SetBindGroup(0, _bind);
            pass.Dispatch((uint) ((count + 63) / 64));
            pass.End();
            pass.Release();

            _drawSlots = count;
        }

        /// <summary>Issue this pass's culled draws into an open render pass (call after the arena's geometry is
        /// bound). One <c>MultiDrawIndexedIndirectCount</c> where supported, else one <c>DrawIndexedIndirect</c>
        /// per resident slot (culled slots are zero-command no-ops).</summary>
        public void Draw(RenderPass pass)
        {
            if (_drawSlots == 0) return;
            if (Compacted)
            {
                pass.MultiDrawIndexedIndirectCount(_indirect, 0, _count, 0, (uint) _maxDraws);
            }
            else
            {
                for (var i = 0; i < _drawSlots; i++)
                    pass.DrawIndexedIndirect(_indirect, (ulong) (i * IndirectStride));
            }
        }

        // Frustum plane in the cull shader's convention: xyz = normal, w = offset, inside = dot(n,p)+w >= 0.
        private static Vector4 Plane(Frustum f, int i)
        {
            var pl = f.Planes[i];
            return new Vector4(pl.Normal.X, pl.Normal.Y, pl.Normal.Z, pl.D);
        }

        public void Dispose()
        {
            _bind?.Dispose();
            _params.Dispose();
            _indirect.Dispose();
            _count.Dispose();
        }
    }
}

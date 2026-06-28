using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MinecraftClone3API.Graphics.Rhi;
using Silk.NET.Maths;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// A coalescing first-fit suballocator over a 1-D element range. Pure CPU bookkeeping; the owner
    /// (<see cref="ChunkMeshArena"/>) keeps the backing GPU buffer in sync. Touched main-thread only.
    /// </summary>
    internal sealed class RangeAllocator
    {
        private struct FreeRange { public long Off; public long Len; }
        private readonly List<FreeRange> _free = new List<FreeRange>();
        public long Capacity { get; private set; }

        public RangeAllocator(long capacity)
        {
            Capacity = capacity;
            _free.Add(new FreeRange {Off = 0, Len = capacity});
        }

        /// <summary>Returns the offset of a free range of <paramref name="count"/> elements, or -1 if none fits.</summary>
        public long Allocate(long count)
        {
            for (var i = 0; i < _free.Count; i++)
            {
                if (_free[i].Len < count) continue;
                var off = _free[i].Off;
                if (_free[i].Len == count) _free.RemoveAt(i);
                else _free[i] = new FreeRange {Off = off + count, Len = _free[i].Len - count};
                return off;
            }
            return -1;
        }

        public void Free(long off, long count)
        {
            if (count <= 0) return;
            var i = 0;
            while (i < _free.Count && _free[i].Off < off) i++;
            _free.Insert(i, new FreeRange {Off = off, Len = count});

            // Coalesce with previous then next.
            if (i > 0 && _free[i - 1].Off + _free[i - 1].Len == off)
            {
                _free[i - 1] = new FreeRange {Off = _free[i - 1].Off, Len = _free[i - 1].Len + _free[i].Len};
                _free.RemoveAt(i);
                i--;
            }
            if (i + 1 < _free.Count && _free[i].Off + _free[i].Len == _free[i + 1].Off)
            {
                _free[i] = new FreeRange {Off = _free[i].Off, Len = _free[i].Len + _free[i + 1].Len};
                _free.RemoveAt(i + 1);
            }
        }

        /// <summary>Adds the newly-grown tail [old capacity, newCapacity) to the free list.</summary>
        public void Grow(long newCapacity)
        {
            if (newCapacity <= Capacity) return;
            Free(Capacity, newCapacity - Capacity);
            Capacity = newCapacity;
        }
    }

    /// <summary>
    /// Per-chunk draw metadata published to the GPU (one element per resident slot). The <c>Cull</c> compute
    /// shader frustum-tests the chunk's 16³ AABB (<see cref="MinCorner"/> + 16) and, if visible, appends a
    /// <c>DrawIndexedIndirect</c> command. <see cref="IndexCount"/> == 0 marks an empty / freed slot the cull
    /// skips. Layout (and the 32-byte stride) must match <c>ChunkMeta</c> in <c>Cull.compute.wgsl</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    internal struct ChunkMeta
    {
        public Vector3 MinCorner;
        public uint IndexCount;
        public uint FirstIndex;
        public int BaseVertex;
        public uint Flags;
        public uint Pad;
    }

    /// <summary>
    /// Shared vertex/index "arena" for OPAQUE chunk (or LOD) meshes. Every chunk's opaque mesh occupies a
    /// sub-range of one big set of GPU buffers, and the arena publishes a <see cref="ChunkMeta"/> storage
    /// buffer — one entry per resident chunk — that the <c>Cull</c> compute shader reads to build the indirect
    /// draw list. The whole visible opaque set then draws with ONE <c>MultiDrawIndexedIndirectCount</c> per
    /// pass (geometry + shadow), and the CPU never builds a visible set (GPU-driven culling). Positions are
    /// baked world-space at mesh time, so no per-chunk model matrix is needed.
    ///
    /// <para>Buffer creation + <see cref="GpuBuffer.QueueWrite{T}"/> are thread-safe in wgpu-native, so uploads
    /// can run off the main thread (see <c>docs/threading.md</c>); growth runs on a transient command encoder
    /// submitted immediately, independent of the frame encoder. The shared <see cref="RangeAllocator"/> is the
    /// allocation source of truth.</para>
    /// </summary>
    public sealed class ChunkMeshArena : IDisposable
    {
        /// <summary>A chunk's reserved sub-range. IndexCount == 0 means "no opaque geometry / unallocated".
        /// <see cref="MetaSlot"/> is the chunk's index in the published <see cref="ChunkMeta"/> array.</summary>
        public struct Allocation
        {
            public int VertexOffset;
            public int VertexCount;
            public int IndexOffset;
            public int IndexCount;
            public int MetaSlot;
        }

        // 5 parallel vertex streams (must mirror MeshBuffer's packed layout + WorldGeometry.wgsl vertex slots) +
        // 1 index stream: 0 pos float3 (12), 1 uv float2 (8), 2 packed uint (4), 3 tint RGBA8 (4), 4 light RGBA8 (4).
        private static readonly int[] AttribBytes = {12, 8, 4, 4, 4};

        // Matches the 32-byte ChunkMeta WGSL element stride (see the struct above).
        private const int MetaBytes = 32;

        /// <summary>The five vertex-buffer layouts a geometry pipeline drawing from this arena declares (one
        /// attribute per slot, matching <c>WorldGeometry.wgsl</c>). The shadow pipeline uses only slot 0.</summary>
        public static readonly VertexBufferDesc[] GeometryVertexLayout =
        {
            new VertexBufferDesc(12, new[] { new VertexAttr(0, VertexFormat.Float32x3, 0) }),
            new VertexBufferDesc(8, new[] { new VertexAttr(1, VertexFormat.Float32x2, 0) }),
            new VertexBufferDesc(4, new[] { new VertexAttr(2, VertexFormat.Uint32, 0) }),
            new VertexBufferDesc(4, new[] { new VertexAttr(3, VertexFormat.Unorm8x4, 0) }),
            new VertexBufferDesc(4, new[] { new VertexAttr(4, VertexFormat.Unorm8x4, 0) }),
        };

        public static readonly VertexBufferDesc[] ShadowVertexLayout =
        {
            new VertexBufferDesc(12, new[] { new VertexAttr(0, VertexFormat.Float32x3, 0) }),
        };

        private readonly GpuBuffer[] _vbo = new GpuBuffer[5];
        private GpuBuffer _ibo;

        private readonly RangeAllocator _vertexAlloc;
        private readonly RangeAllocator _indexAlloc;

        // CPU mirror of the published metadata. Freed slots are reused via _freeSlots; the GPU buffer covers
        // [0, _metas.Count), and the cull dispatch skips any slot with IndexCount == 0.
        private readonly List<ChunkMeta> _metas = new List<ChunkMeta>();
        private readonly Stack<int> _freeSlots = new Stack<int>();
        private GpuBuffer _metaBuffer;
        private int _metaCapacity;
        private bool _metaDirty;

        private readonly string _label;

        /// <summary>The storage buffer the cull compute reads (<c>array&lt;ChunkMeta&gt;</c>). The reference
        /// changes when the buffer grows, so a cached cull bind group must be rebuilt — compare against this.</summary>
        public GpuBuffer MetaBuffer => _metaBuffer;

        /// <summary>Number of published slots (the cull dispatch covers all of them; empty slots are skipped).</summary>
        public int MetaCount => _metas.Count;

        public ChunkMeshArena(string label = "chunkArena", int initialVertices = 256 * 1024,
            int initialIndices = 384 * 1024, int initialChunks = 4096)
        {
            _label = label;
            for (var i = 0; i < 5; i++)
                _vbo[i] = new GpuBuffer((ulong)((long)initialVertices * AttribBytes[i]),
                    BufferUsage.Vertex | BufferUsage.CopyDst | BufferUsage.CopySrc, $"{label}.vbo{i}");
            _ibo = new GpuBuffer((ulong)((long)initialIndices * sizeof(uint)),
                BufferUsage.Index | BufferUsage.CopyDst | BufferUsage.CopySrc, $"{label}.ibo");

            _metaCapacity = initialChunks;
            _metaBuffer = new GpuBuffer((ulong)(_metaCapacity * MetaBytes),
                BufferUsage.Storage | BufferUsage.CopyDst, $"{label}.meta");

            _vertexAlloc = new RangeAllocator(initialVertices);
            _indexAlloc = new RangeAllocator(initialIndices);
        }

        /// <summary>Uploads a chunk's freshly-meshed opaque CPU buffers into the arena, (re)allocating its
        /// sub-range as needed, and publishes its <see cref="ChunkMeta"/>. <paramref name="minCorner"/> is the
        /// chunk's world-space minimum corner (its 16³ AABB origin) for GPU culling.</summary>
        public Allocation Upload(Allocation existing, MeshBuffer mesh, Vector3 minCorner)
        {
            var vCount = mesh.VertexCount;
            var iCount = mesh.IndicesCount;

            if (iCount == 0 || vCount == 0)
            {
                Free(existing);
                return default;
            }

            // Reuse the existing range when it fits exactly; otherwise free and allocate fresh. A chunk with no
            // prior geometry has IndexCount == 0 (its MetaSlot is meaningless), so don't reuse slot 0 by mistake.
            if (!(existing.IndexCount == iCount && existing.VertexCount == vCount))
            {
                var slot = existing.IndexCount > 0 ? existing.MetaSlot : -1;
                Free(existing, releaseSlot: false);
                var vOff = AllocVertices(vCount);
                var iOff = AllocIndices(iCount);
                existing = new Allocation
                {
                    VertexOffset = (int) vOff, VertexCount = vCount,
                    IndexOffset = (int) iOff, IndexCount = iCount,
                    MetaSlot = slot >= 0 ? slot : AllocSlot(),
                };
            }

            UploadList(_vbo[0], existing.VertexOffset, AttribBytes[0], mesh.Positions);
            UploadList(_vbo[1], existing.VertexOffset, AttribBytes[1], mesh.Uvs);
            UploadList(_vbo[2], existing.VertexOffset, AttribBytes[2], mesh.Packed);
            UploadList(_vbo[3], existing.VertexOffset, AttribBytes[3], mesh.Colors);
            UploadList(_vbo[4], existing.VertexOffset, AttribBytes[4], mesh.Lights);

            _ibo.QueueWrite<uint>(CollectionsMarshal.AsSpan(mesh.Indices), (ulong)((long)existing.IndexOffset * sizeof(uint)));

            _metas[existing.MetaSlot] = new ChunkMeta
            {
                MinCorner = minCorner,
                IndexCount = (uint) iCount,
                FirstIndex = (uint) existing.IndexOffset,
                BaseVertex = existing.VertexOffset,
                Flags = 0,
                Pad = 0,
            };
            _metaDirty = true;

            return existing;
        }

        public void Free(Allocation a) => Free(a, releaseSlot: true);

        private void Free(Allocation a, bool releaseSlot)
        {
            if (a.IndexCount == 0) return;
            _vertexAlloc.Free(a.VertexOffset, a.VertexCount);
            _indexAlloc.Free(a.IndexOffset, a.IndexCount);
            if (a.MetaSlot >= 0)
            {
                // Zero the slot so the cull skips it; only return it to the free list when the chunk is gone
                // for good (a reupload keeps its slot so the meta index in its Allocation stays valid).
                _metas[a.MetaSlot] = default;
                _metaDirty = true;
                if (releaseSlot) _freeSlots.Push(a.MetaSlot);
            }
        }

        private int AllocSlot()
        {
            if (_freeSlots.Count > 0) return _freeSlots.Pop();
            _metas.Add(default);
            return _metas.Count - 1;
        }

        /// <summary>Re-upload the published metadata to the GPU if it changed since the last frame, growing the
        /// storage buffer (and signalling a bind-group rebuild via a new <see cref="MetaBuffer"/>) when needed.
        /// Called once per frame before the cull dispatch.</summary>
        public void FlushMeta()
        {
            if (!_metaDirty) return;
            _metaDirty = false;

            if (_metas.Count > _metaCapacity)
            {
                while (_metas.Count > _metaCapacity) _metaCapacity *= 2;
                _metaBuffer.Dispose();
                _metaBuffer = new GpuBuffer((ulong)(_metaCapacity * MetaBytes),
                    BufferUsage.Storage | BufferUsage.CopyDst, $"{_label}.meta");
            }
            if (_metas.Count > 0)
                _metaBuffer.QueueWrite<ChunkMeta>(CollectionsMarshal.AsSpan(_metas));
        }

        /// <summary>Bind the five vertex streams + index buffer for the G-buffer geometry pass.</summary>
        public void BindGeometry(RenderPass pass)
        {
            for (uint i = 0; i < 5; i++) pass.SetVertexBuffer(i, _vbo[i]);
            pass.SetIndexBuffer(_ibo, IndexFormat.Uint32);
        }

        /// <summary>Bind position-only (slot 0) + index for the depth-only shadow pass.</summary>
        public void BindShadow(RenderPass pass)
        {
            pass.SetVertexBuffer(0, _vbo[0]);
            pass.SetIndexBuffer(_ibo, IndexFormat.Uint32);
        }

        private long AllocVertices(int count)
        {
            var off = _vertexAlloc.Allocate(count);
            if (off >= 0) return off;

            var oldCap = _vertexAlloc.Capacity;
            var newCap = oldCap;
            while (newCap - oldCap < count) newCap *= 2;
            GrowVertexBuffers(newCap);
            _vertexAlloc.Grow(newCap);
            return _vertexAlloc.Allocate(count);
        }

        private long AllocIndices(int count)
        {
            var off = _indexAlloc.Allocate(count);
            if (off >= 0) return off;

            var oldCap = _indexAlloc.Capacity;
            var newCap = oldCap;
            while (newCap - oldCap < count) newCap *= 2;
            GrowIndexBuffer(newCap);
            _indexAlloc.Grow(newCap);
            return _indexAlloc.Allocate(count);
        }

        private void GrowVertexBuffers(long newCap)
        {
            var enc = GpuCommandEncoder.Create($"{_label}.growVertex");
            for (var i = 0; i < 5; i++)
                _vbo[i].Grow(enc, (ulong)(newCap * AttribBytes[i]), (ulong)(_vertexAlloc.Capacity * AttribBytes[i]));
            enc.SubmitImmediate($"{_label}.growVertex");
        }

        private void GrowIndexBuffer(long newCap)
        {
            var enc = GpuCommandEncoder.Create($"{_label}.growIndex");
            _ibo.Grow(enc, (ulong)(newCap * sizeof(uint)), (ulong)(_indexAlloc.Capacity * sizeof(uint)));
            enc.SubmitImmediate($"{_label}.growIndex");
        }

        private static void UploadList<T>(GpuBuffer buffer, int vertexOffset, int elemBytes, List<T> data)
            where T : unmanaged
            => buffer.QueueWrite<T>(CollectionsMarshal.AsSpan(data), (ulong)((long)vertexOffset * elemBytes));

        public void Dispose()
        {
            for (var i = 0; i < 5; i++) _vbo[i]?.Dispose();
            _ibo?.Dispose();
            _metaBuffer?.Dispose();
        }
    }
}

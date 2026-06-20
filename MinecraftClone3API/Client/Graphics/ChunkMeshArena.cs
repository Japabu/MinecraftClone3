using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// A coalescing first-fit suballocator over a 1-D element range. Pure CPU bookkeeping; the owner
    /// (<see cref="ChunkMeshArena"/>) keeps the backing GL buffer in sync. Touched main-thread only.
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
    /// Shared vertex/index "arena" for all OPAQUE chunk meshes. Every chunk's opaque mesh occupies a
    /// sub-range of one big set of GL buffers, so the whole visible opaque set is drawn with ONE
    /// <c>glMultiDrawElementsBaseVertex</c> per pass (geometry + shadow) instead of ~one bind+draw per chunk.
    /// That per-chunk draw-call overhead — not fill — was the steady-state GPU/CPU bottleneck (see CLAUDE.md
    /// rendering notes). Positions are baked world-space at mesh time, so no per-chunk model matrix is needed
    /// (a single shared draw can't carry one). All access is main-thread (upload in the client's upload loop,
    /// free in its dispose drain), so no locking — GL stays on the main thread (Invariant 1).
    /// </summary>
    public sealed class ChunkMeshArena : IDisposable
    {
        /// <summary>A chunk's reserved sub-range. IndexCount == 0 means "no opaque geometry / unallocated".</summary>
        public struct Allocation
        {
            public int VertexOffset;
            public int VertexCount;
            public int IndexOffset;
            public int IndexCount;
        }

        // 5 parallel vertex streams (must mirror MeshBuffer's packed layout) + 1 index stream:
        // 0 pos float3 (12), 1 uv float2 (8), 2 packed uint (4), 3 tint RGBA8 (4), 4 light RGBA8 (4).
        private static readonly int[] AttribBytes = {12, 8, 4, 4, 4};

        private readonly int[] _vbo = new int[5];
        private int _ibo;
        private int _geometryVao; // all 5 attributes — the G-buffer geometry pass
        private int _shadowVao;   // position only — the depth-only shadow pass (skips fetching 60 unused bytes/vertex)

        private readonly RangeAllocator _vertexAlloc;
        private readonly RangeAllocator _indexAlloc;

        // Reused multidraw scratch (grown as needed) so the per-frame draw allocates nothing.
        private int[] _counts = new int[1024];
        private IntPtr[] _offsets = new IntPtr[1024];
        private int[] _baseVertices = new int[1024];

        public ChunkMeshArena(int initialVertices = 256 * 1024, int initialIndices = 384 * 1024)
        {
            GL.GenBuffers(5, _vbo);
            _ibo = GL.GenBuffer();
            for (var i = 0; i < 5; i++)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo[i]);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr) ((long) initialVertices * AttribBytes[i]), IntPtr.Zero,
                    BufferUsageHint.DynamicDraw);
            }
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ibo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr) ((long) initialIndices * sizeof(uint)), IntPtr.Zero,
                BufferUsageHint.DynamicDraw);

            _geometryVao = GL.GenVertexArray();
            _shadowVao = GL.GenVertexArray();
            SetupVaos();

            _vertexAlloc = new RangeAllocator(initialVertices);
            _indexAlloc = new RangeAllocator(initialIndices);

            GraphicsDebug.Label(ObjectLabelIdentifier.VertexArray, _geometryVao, "ChunkArenaGeometryVAO");
            GraphicsDebug.Label(ObjectLabelIdentifier.VertexArray, _shadowVao, "ChunkArenaShadowVAO");
        }

        private void SetupVaos()
        {
            GL.BindVertexArray(_geometryVao);
            BindVertexFormat();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ibo);

            GL.BindVertexArray(_shadowVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo[0]);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 0, 0);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ibo);

            GL.BindVertexArray(0);
        }

        /// <summary>Wires the 5 packed attribute streams to the currently-bound VAO (shared by
        /// <see cref="VertexArrayObject"/> for the transparent path — keep the two in sync).</summary>
        internal static void BindVertexFormat(int posVbo, int uvVbo, int packedVbo, int colorVbo, int lightVbo)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, posVbo);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 0, 0);

            GL.BindBuffer(BufferTarget.ArrayBuffer, uvVbo);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 0, 0);

            GL.BindBuffer(BufferTarget.ArrayBuffer, packedVbo);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribIPointer(2, 1, VertexAttribIntegerType.UnsignedInt, 0, 0);

            GL.BindBuffer(BufferTarget.ArrayBuffer, colorVbo);
            GL.EnableVertexAttribArray(3);
            GL.VertexAttribPointer(3, 4, VertexAttribPointerType.UnsignedByte, true, 0, 0);

            GL.BindBuffer(BufferTarget.ArrayBuffer, lightVbo);
            GL.EnableVertexAttribArray(4);
            GL.VertexAttribPointer(4, 4, VertexAttribPointerType.UnsignedByte, true, 0, 0);
        }

        private void BindVertexFormat() =>
            BindVertexFormat(_vbo[0], _vbo[1], _vbo[2], _vbo[3], _vbo[4]);

        /// <summary>Uploads a chunk's freshly-meshed opaque CPU buffers into the arena, (re)allocating its
        /// sub-range as needed, and returns the updated allocation handle. Main-thread GL.</summary>
        public Allocation Upload(Allocation existing, MeshBuffer mesh)
        {
            var vCount = mesh.VertexCount;
            var iCount = mesh.IndicesCount;

            if (iCount == 0 || vCount == 0)
            {
                Free(existing);
                return default;
            }

            // Reuse the existing range when it fits exactly; otherwise free and allocate fresh.
            if (!(existing.IndexCount == iCount && existing.VertexCount == vCount))
            {
                Free(existing);
                var vOff = AllocVertices(vCount);
                var iOff = AllocIndices(iCount);
                existing = new Allocation
                {
                    VertexOffset = (int) vOff, VertexCount = vCount,
                    IndexOffset = (int) iOff, IndexCount = iCount
                };
            }

            UploadList(_vbo[0], existing.VertexOffset, AttribBytes[0], mesh.Positions);
            UploadList(_vbo[1], existing.VertexOffset, AttribBytes[1], mesh.Uvs);
            UploadList(_vbo[2], existing.VertexOffset, AttribBytes[2], mesh.Packed);
            UploadList(_vbo[3], existing.VertexOffset, AttribBytes[3], mesh.Colors);
            UploadList(_vbo[4], existing.VertexOffset, AttribBytes[4], mesh.Lights);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ibo);
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, (IntPtr) ((long) existing.IndexOffset * sizeof(uint)),
                (IntPtr) ((long) iCount * sizeof(uint)),
                ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(mesh.Indices)));

            return existing;
        }

        public void Free(Allocation a)
        {
            if (a.IndexCount == 0) return;
            _vertexAlloc.Free(a.VertexOffset, a.VertexCount);
            _indexAlloc.Free(a.IndexOffset, a.IndexCount);
        }

        private long AllocVertices(int count)
        {
            var off = _vertexAlloc.Allocate(count);
            if (off >= 0) return off;

            var newCap = _vertexAlloc.Capacity;
            while (newCap - HighWater(_vertexAlloc) < count) newCap *= 2; // ensure the grown tail can hold it
            GrowVertexBuffers(newCap);
            _vertexAlloc.Grow(newCap);
            return _vertexAlloc.Allocate(count);
        }

        private long AllocIndices(int count)
        {
            var off = _indexAlloc.Allocate(count);
            if (off >= 0) return off;

            var newCap = _indexAlloc.Capacity;
            while (newCap - HighWater(_indexAlloc) < count) newCap *= 2;
            GrowIndexBuffer(newCap);
            _indexAlloc.Grow(newCap);
            return _indexAlloc.Allocate(count);
        }

        // Worst-case "could the doubled tail hold it" guard: a coalesced free list might already have a big
        // tail, but doubling capacity always gives at least the old capacity of fresh contiguous tail, which
        // is >= any single chunk mesh in practice; the loop just guarantees termination for huge meshes.
        private static long HighWater(RangeAllocator a) => a.Capacity;

        private void GrowVertexBuffers(long newCap)
        {
            for (var i = 0; i < 5; i++)
            {
                var newId = GL.GenBuffer();
                GL.BindBuffer(BufferTarget.CopyWriteBuffer, newId);
                GL.BufferData(BufferTarget.CopyWriteBuffer, (IntPtr) (newCap * AttribBytes[i]), IntPtr.Zero,
                    BufferUsageHint.DynamicDraw);
                GL.BindBuffer(BufferTarget.CopyReadBuffer, _vbo[i]);
                GL.CopyBufferSubData(BufferTarget.CopyReadBuffer, BufferTarget.CopyWriteBuffer, IntPtr.Zero, IntPtr.Zero,
                    (IntPtr) (_vertexAlloc.Capacity * AttribBytes[i]));
                GL.DeleteBuffer(_vbo[i]);
                _vbo[i] = newId;
            }
            SetupVaos();
        }

        private void GrowIndexBuffer(long newCap)
        {
            var newId = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.CopyWriteBuffer, newId);
            GL.BufferData(BufferTarget.CopyWriteBuffer, (IntPtr) (newCap * sizeof(uint)), IntPtr.Zero,
                BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.CopyReadBuffer, _ibo);
            GL.CopyBufferSubData(BufferTarget.CopyReadBuffer, BufferTarget.CopyWriteBuffer, IntPtr.Zero, IntPtr.Zero,
                (IntPtr) (_indexAlloc.Capacity * sizeof(uint)));
            GL.DeleteBuffer(_ibo);
            _ibo = newId;
            SetupVaos();
        }

        private static void UploadList<T>(int buffer, int vertexOffset, int elemBytes, List<T> data) where T : struct
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            GL.BufferSubData(BufferTarget.ArrayBuffer, (IntPtr) ((long) vertexOffset * elemBytes),
                (IntPtr) ((long) data.Count * elemBytes),
                ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(data)));
        }

        /// <summary>Draws the given chunks' opaque sub-ranges with one multidraw. <paramref name="shadow"/>
        /// selects the position-only VAO (depth pass). Returns the number of sub-draws issued.</summary>
        public int Draw(List<ChunkRenderData> chunks, bool shadow)
        {
            var n = chunks.Count;
            if (n > _counts.Length)
            {
                var cap = _counts.Length;
                while (cap < n) cap *= 2;
                _counts = new int[cap];
                _offsets = new IntPtr[cap];
                _baseVertices = new int[cap];
            }

            var draws = 0;
            for (var i = 0; i < n; i++)
            {
                var a = chunks[i].OpaqueAlloc;
                if (a.IndexCount == 0) continue;
                _counts[draws] = a.IndexCount;
                _offsets[draws] = (IntPtr) ((long) a.IndexOffset * sizeof(uint));
                _baseVertices[draws] = a.VertexOffset;
                draws++;
            }
            if (draws == 0) return 0;

            GL.BindVertexArray(shadow ? _shadowVao : _geometryVao);
            GL.MultiDrawElementsBaseVertex(PrimitiveType.Triangles, _counts, DrawElementsType.UnsignedInt,
                _offsets, draws, _baseVertices);
            return draws;
        }

        public void Dispose()
        {
            GL.DeleteVertexArray(_geometryVao);
            GL.DeleteVertexArray(_shadowVao);
            GL.DeleteBuffers(5, _vbo);
            GL.DeleteBuffer(_ibo);
        }
    }
}

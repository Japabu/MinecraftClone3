using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics.Rhi;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// A <see cref="MeshBuffer"/> backed by its own per-chunk RHI vertex/index buffers, for the TRANSPARENT
    /// (translucent) block faces of one chunk. Unlike the opaque mesh — which uploads into the shared
    /// <see cref="ChunkMeshArena"/> and draws with one batched multidraw — translucent faces need an
    /// independent per-frame back-to-front sort, so each chunk keeps its own buffers and re-uploads the index
    /// stream whenever the camera moves (see <see cref="Sort"/>). The vertex format matches
    /// <see cref="ChunkMeshArena.GeometryVertexLayout"/>, so it shares the geometry/transparent pipeline.
    /// </summary>
    public class SortedVertexArrayObject : MeshBuffer, IDisposable
    {
        public override bool Sorted => true;

        /// <summary>Uploaded index count (faces * 6). Zero while the chunk has no transparent geometry.</summary>
        public int UploadedCount;

        private struct FaceInfo : IComparable<FaceInfo>
        {
            public Vector3 Position;
            public int BaseVertex;
            public bool Flipped;

            public int CompareTo(FaceInfo other)
            {
                var cameraPos = PlayerController.Camera.Position;
                return (int) ((cameraPos - other.Position).LengthSquared * 10000 - (cameraPos - Position).LengthSquared * 10000);
            }
        }

        // Five parallel vertex streams (pos/uv/packed/tint/light) + one index stream, mirroring the arena's
        // GeometryVertexLayout. Created lazily on the first upload and recreated when a remesh outgrows them.
        private static readonly int[] AttribBytes = {12, 8, 4, 4, 4};
        private readonly GpuBuffer[] _vbo = new GpuBuffer[5];
        private GpuBuffer _ibo;

        // Allocated lazily on the first transparent face. Every ChunkRenderData owns a transparent VAO,
        // but most chunks are fully opaque and never add a face — eager 1024-capacity lists meant ~24 KB
        // of empty backing arrays per streamed chunk that then stayed resident, the dominant main-thread
        // allocation while moving. Null until needed keeps opaque chunks allocation-free here.
        private List<FaceInfo> _faceInfos;
        private FaceInfo[] _uploadedFaces;
        private List<uint> _sortedIndices;

        public override void AddFace(int baseVertex, bool flipped, Vector3 faceMiddle)
        {
            if (_faceInfos == null)
                _faceInfos = new List<FaceInfo>(1024);

            _faceInfos.Add(new FaceInfo {Position = faceMiddle, BaseVertex = baseVertex, Flipped = flipped});
        }

        public void Upload()
        {
            if (_faceInfos == null || _faceInfos.Count <= 0)
            {
                UploadedCount = 0;
                return;
            }

            // Packed 32-byte vertex (see MeshBuffer), same layout the geometry arena uses.
            UploadStream(0, AttribBytes[0], Positions);
            UploadStream(1, AttribBytes[1], Uvs);
            UploadStream(2, AttribBytes[2], Packed);
            UploadStream(3, AttribBytes[3], Colors);
            UploadStream(4, AttribBytes[4], Lights);

            UploadedCount = _faceInfos.Count * 6;

            if (_uploadedFaces == null || _uploadedFaces.Length != _faceInfos.Count)
                _uploadedFaces = new FaceInfo[_faceInfos.Count];
            _faceInfos.CopyTo(_uploadedFaces);

            BuildIndices();

            EnsureIndexBuffer(_sortedIndices.Count);
            _ibo.QueueWrite<uint>(CollectionsMarshal.AsSpan(_sortedIndices));
        }

        public override void Clear()
        {
            base.Clear();

            _faceInfos?.Clear();
        }

        public void Sort()
        {
            if (_uploadedFaces == null || _uploadedFaces.Length == 0) return;

            Array.Sort(_uploadedFaces);
            BuildIndices();

            _ibo.QueueWrite<uint>(CollectionsMarshal.AsSpan(_sortedIndices));
        }

        /// <summary>Bind the five vertex streams (slots 0-4) + the index buffer, matching
        /// <see cref="ChunkMeshArena.GeometryVertexLayout"/> / <see cref="IndexFormat.Uint32"/>.</summary>
        public void Bind(RenderPass pass)
        {
            for (uint i = 0; i < 5; i++) pass.SetVertexBuffer(i, _vbo[i]);
            pass.SetIndexBuffer(_ibo, IndexFormat.Uint32);
        }

        public void Draw(RenderPass pass)
        {
            if (UploadedCount <= 0) return;
            Bind(pass);
            pass.DrawIndexed((uint) UploadedCount);
        }

        public void Dispose()
        {
            for (var i = 0; i < 5; i++) { _vbo[i]?.Dispose(); _vbo[i] = null; }
            _ibo?.Dispose();
            _ibo = null;
        }

        private void UploadStream<T>(int slot, int elemBytes, List<T> data) where T : unmanaged
        {
            var bytes = (ulong)((long) data.Count * elemBytes);
            if (_vbo[slot] == null || _vbo[slot].Size < bytes)
            {
                _vbo[slot]?.Dispose();
                _vbo[slot] = new GpuBuffer(bytes, BufferUsage.Vertex | BufferUsage.CopyDst, $"transparentVbo{slot}");
            }
            _vbo[slot].QueueWrite<T>(CollectionsMarshal.AsSpan(data));
        }

        private void EnsureIndexBuffer(int indexCount)
        {
            var bytes = (ulong)((long) indexCount * sizeof(uint));
            if (_ibo == null || _ibo.Size < bytes)
            {
                _ibo?.Dispose();
                _ibo = new GpuBuffer(bytes, BufferUsage.Index | BufferUsage.CopyDst, "transparentIbo");
            }
        }

        private void BuildIndices()
        {
            if (_sortedIndices == null)
                _sortedIndices = new List<uint>(_uploadedFaces.Length * 6);
            else
                _sortedIndices.Clear();

            foreach (var face in _uploadedFaces)
            {
                var pattern = face.Flipped ? FlippedFaceIndices : FaceIndices;
                for (var i = 0; i < pattern.Length; i++)
                    _sortedIndices.Add((uint) (pattern[i] + face.BaseVertex));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MinecraftClone3API.Entities;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public class SortedVertexArrayObject : VertexArrayObject
    {
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

        public override void Upload()
        {
            if (_faceInfos == null || _faceInfos.Count <= 0)
            {
                UploadedCount = 0;
                return;
            }

            // A re-upload orphans each buffer so the GL call never stalls on the in-flight mesh; the
            // attribute pointers are wired up only on the first upload. The index buffer is DynamicDraw
            // because Sort() rewrites it (BufferSubData) every frame for back-to-front transparency.
            var firstUpload = UploadedCount == 0;
            GL.BindVertexArray(VaoId);

            GlBuffer.UploadArray(BufferTarget.ArrayBuffer, BufferIds[0], Positions, Vector3.SizeInBytes, firstUpload);
            if (firstUpload)
            {
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 0, 0);
            }

            GlBuffer.UploadArray(BufferTarget.ArrayBuffer, BufferIds[1], TexCoords, Vector4.SizeInBytes, firstUpload);
            if (firstUpload)
            {
                GL.EnableVertexAttribArray(1);
                GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 0, 0);
            }

            GlBuffer.UploadArray(BufferTarget.ArrayBuffer, BufferIds[2], Normals, Vector4.SizeInBytes, firstUpload);
            if (firstUpload)
            {
                GL.EnableVertexAttribArray(2);
                GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, 0, 0);
            }

            GlBuffer.UploadArray(BufferTarget.ArrayBuffer, BufferIds[3], Colors, Vector3.SizeInBytes, firstUpload);
            if (firstUpload)
            {
                GL.EnableVertexAttribArray(3);
                GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, 0, 0);
            }

            GlBuffer.UploadArray(BufferTarget.ArrayBuffer, BufferIds[4], Lights, Vector3.SizeInBytes, firstUpload);
            if (firstUpload)
            {
                GL.EnableVertexAttribArray(4);
                GL.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, 0, 0);
            }

            UploadedCount = _faceInfos.Count * 6;

            if (_uploadedFaces == null || _uploadedFaces.Length != _faceInfos.Count)
                _uploadedFaces = new FaceInfo[_faceInfos.Count];
            _faceInfos.CopyTo(_uploadedFaces);

            BuildIndices();

            GlBuffer.UploadArray(BufferTarget.ElementArrayBuffer, IndicesId, _sortedIndices, sizeof(uint),
                firstUpload, BufferUsageHint.DynamicDraw);
        }

        public override void Clear()
        {
            base.Clear();

            _faceInfos?.Clear();
        }

        public override void Sort()
        {
            if (_uploadedFaces == null) return;

            Array.Sort(_uploadedFaces);
            BuildIndices();

            //TODO: Use streaming BufferSubData too slow
            GL.BindVertexArray(VaoId);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, IndicesId);
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, (IntPtr) (_sortedIndices.Count * sizeof(uint)), ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_sortedIndices)));
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

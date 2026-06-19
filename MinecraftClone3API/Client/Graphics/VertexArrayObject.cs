using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public class VertexArrayObject : IDisposable
    {
        protected static readonly uint[] FaceIndices = {2, 1, 0, 2, 3, 1};
        protected static readonly uint[] FlippedFaceIndices = {0, 2, 3, 0, 3, 1};

        protected readonly int VaoId;
        protected readonly int[] BufferIds = new int[5];
        protected readonly int IndicesId;

        protected List<Vector3> Positions;
        protected List<Vector4> TexCoords;
        protected List<Vector4> Normals;
        protected List<Vector3> Colors;
        protected List<Vector3> Lights;
        protected List<uint> Indices;

        public int UploadedCount;
        public int VertexCount => (Positions?.Count).GetValueOrDefault();
        public int IndicesCount => (Indices?.Count).GetValueOrDefault();

        public VertexArrayObject()
        {
            VaoId = GL.GenVertexArray();
            GL.GenBuffers(BufferIds.Length, BufferIds);
            IndicesId = GL.GenBuffer();
        }

        public virtual void Add(Vector3 position, Vector4 texCoord, Vector4 normal, Vector3 color, Vector3 light)
        {
            if (Positions == null)
            {
                Positions = VaoBufferPool.RentVector3();
                TexCoords = VaoBufferPool.RentVector4();
                Normals = VaoBufferPool.RentVector4();
                Colors = VaoBufferPool.RentVector3();
                Lights = VaoBufferPool.RentVector3();

                Indices = VaoBufferPool.RentUint();
            }

            Positions.Add(position);
            TexCoords.Add(texCoord);
            Normals.Add(normal);
            Colors.Add(color);
            Lights.Add(light);
        }

        public virtual void AddFace(uint[] indices, Vector3 faceMiddle) => Indices.AddRange(indices);

        /// <summary>Appends the six indices for the four vertices just <see cref="Add"/>ed, generated
        /// in place from the shared winding pattern so the mesher allocates no per-face index array.</summary>
        public virtual void AddFace(int baseVertex, bool flipped, Vector3 faceMiddle)
        {
            var pattern = flipped ? FlippedFaceIndices : FaceIndices;
            for (var i = 0; i < pattern.Length; i++)
                Indices.Add((uint) (pattern[i] + baseVertex));
        }

        public virtual void Upload()
        {
            if (IndicesCount <= 0)
            {
                UploadedCount = 0;
                return;
            }

            // A re-upload (UploadedCount != 0) orphans each buffer so the GL call never stalls waiting for
            // the GPU to finish drawing the in-flight mesh; the attribute pointers are wired up only on the
            // first upload. See GlBuffer.UploadArray.
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

            GlBuffer.UploadArray(BufferTarget.ElementArrayBuffer, IndicesId, Indices, sizeof(uint), firstUpload);

            UploadedCount = Indices.Count;
        }

        public virtual void Draw() => Draw(BeginMode.Triangles);
        public virtual void Draw(BeginMode mode)
        {
            if (UploadedCount <= 0) return;

            GL.BindVertexArray(VaoId);
            GL.DrawElements(mode, UploadedCount, DrawElementsType.UnsignedInt, 0);
        }

        public virtual void Clear()
        {
            VaoBufferPool.Return(Positions);
            VaoBufferPool.Return(TexCoords);
            VaoBufferPool.Return(Normals);
            VaoBufferPool.Return(Colors);
            VaoBufferPool.Return(Lights);
            VaoBufferPool.Return(Indices);

            Positions = null;
            TexCoords = null;
            Normals = null;
            Colors = null;
            Lights = null;
            Indices = null;
        }

        public virtual void Sort()
        {
        }

        public virtual void Dispose()
        {
            GL.DeleteBuffer(IndicesId);
            GL.DeleteBuffers(BufferIds.Length, BufferIds);
            GL.DeleteVertexArray(VaoId);
        }
    }
}
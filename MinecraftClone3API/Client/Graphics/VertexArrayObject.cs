using System;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// A <see cref="MeshBuffer"/> backed by its own GL vertex array + buffers. Used for the per-chunk
    /// TRANSPARENT mesh (which needs an independent per-frame back-to-front index sort, see
    /// <see cref="SortedVertexArrayObject"/>); the opaque mesh instead uploads into the shared
    /// <see cref="ChunkMeshArena"/> and is drawn with one batched multidraw.
    /// </summary>
    public class VertexArrayObject : MeshBuffer, IDisposable
    {
        protected readonly int VaoId;
        protected readonly int[] BufferIds = new int[5];
        protected readonly int IndicesId;

        public int UploadedCount;

        public VertexArrayObject()
        {
            VaoId = GL.GenVertexArray();
            GL.GenBuffers(BufferIds.Length, BufferIds);
            IndicesId = GL.GenBuffer();
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

            GlBuffer.UploadArray(BufferTarget.ArrayBuffer, BufferIds[4], Lights, Vector4.SizeInBytes, firstUpload);
            if (firstUpload)
            {
                GL.EnableVertexAttribArray(4);
                GL.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, 0, 0);
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

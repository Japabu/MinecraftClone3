using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public class SpriteVertexArrayObject
    {
        protected readonly int VaoId;
        protected readonly int[] BufferIds = new int[3];
        protected readonly int IndicesId;

        protected List<Vector2> Positions;
        protected List<Vector2> TexCoords;
        protected List<Vector3> Colors;
        protected List<uint> Indices;

        public int UploadedCount;
        public int VertexCount => (Positions?.Count).GetValueOrDefault();
        public int IndicesCount => (Indices?.Count).GetValueOrDefault();

        public SpriteVertexArrayObject()
        {
            VaoId = GL.GenVertexArray();
            GL.GenBuffers(BufferIds.Length, BufferIds);
            IndicesId = GL.GenBuffer();
        }

        public virtual void Add(Vector2 position, Vector2 texCoord, Vector3 color)
        {
            if (Positions == null)
            {
                Positions = new List<Vector2>(1024);
                TexCoords = new List<Vector2>(1024);
                Colors = new List<Vector3>(1024);

                Indices = new List<uint>(1024);
            }

            Positions.Add(position);
            TexCoords.Add(texCoord);
            Colors.Add(color);
        }

        public virtual void AddFace(uint[] indices) => Indices.AddRange(indices);

        public virtual void Upload()
        {
            if (IndicesCount <= 0)
            {
                UploadedCount = 0;
                return;
            }

            // A re-upload (e.g. GUI text whose string changed) orphans each buffer so the GL call never
            // stalls on the in-flight draw; attribute pointers are wired up only on the first upload.
            var firstUpload = UploadedCount == 0;
            GL.BindVertexArray(VaoId);

            GlBuffer.UploadArray(BufferTarget.ArrayBuffer, BufferIds[0], Positions, Vector2.SizeInBytes, firstUpload);
            if (firstUpload)
            {
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 0, 0);
            }

            GlBuffer.UploadArray(BufferTarget.ArrayBuffer, BufferIds[1], TexCoords, Vector2.SizeInBytes, firstUpload);
            if (firstUpload)
            {
                GL.EnableVertexAttribArray(1);
                GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 0, 0);
            }

            GlBuffer.UploadArray(BufferTarget.ArrayBuffer, BufferIds[2], Colors, Vector3.SizeInBytes, firstUpload);
            if (firstUpload)
            {
                GL.EnableVertexAttribArray(2);
                GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, 0, 0);
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
            Positions = null;
            TexCoords = null;
            Colors = null;
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

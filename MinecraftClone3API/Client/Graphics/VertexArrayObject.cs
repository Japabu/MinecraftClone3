using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public class VertexArrayObject : IDisposable
    {
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
                Positions = new List<Vector3>(1024);
                TexCoords = new List<Vector4>(1024);
                Normals = new List<Vector4>(1024);
                Colors = new List<Vector3>(1024);
                Lights = new List<Vector3>(1024);

                Indices = new List<uint>(1024);
            }

            Positions.Add(position);
            TexCoords.Add(texCoord);
            Normals.Add(normal);
            Colors.Add(color);
            Lights.Add(light);
        }
        
        public virtual void AddFace(uint[] indices, Vector3 faceMiddle) => Indices.AddRange(indices);

        public virtual void Upload()
        {
            if (IndicesCount <= 0)
            {
                UploadedCount = 0;
                return;
            }

            GL.BindVertexArray(VaoId);

            if (UploadedCount == 0)
            {
                //0 positions
                GL.BindBuffer(BufferTarget.ArrayBuffer, BufferIds[0]);
                GL.BufferData(BufferTarget.ArrayBuffer, Positions.Count * Vector3.SizeInBytes, Positions.ToArray(),
                    BufferUsageHint.StaticDraw);
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 0, 0);
                //1 texCoords
                GL.BindBuffer(BufferTarget.ArrayBuffer, BufferIds[1]);
                GL.BufferData(BufferTarget.ArrayBuffer, TexCoords.Count * Vector4.SizeInBytes, TexCoords.ToArray(),
                    BufferUsageHint.StaticDraw);
                GL.EnableVertexAttribArray(1);
                GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 0, 0);
                //2 normals
                GL.BindBuffer(BufferTarget.ArrayBuffer, BufferIds[2]);
                GL.BufferData(BufferTarget.ArrayBuffer, Normals.Count * Vector4.SizeInBytes, Normals.ToArray(),
                    BufferUsageHint.StaticDraw);
                GL.EnableVertexAttribArray(2);
                GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, 0, 0);
                //3 color
                GL.BindBuffer(BufferTarget.ArrayBuffer, BufferIds[3]);
                GL.BufferData(BufferTarget.ArrayBuffer, Colors.Count * Vector3.SizeInBytes, Colors.ToArray(),
                    BufferUsageHint.StaticDraw);
                GL.EnableVertexAttribArray(3);
                GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, 0, 0);
                //4 light
                GL.BindBuffer(BufferTarget.ArrayBuffer, BufferIds[4]);
                GL.BufferData(BufferTarget.ArrayBuffer, Lights.Count * Vector3.SizeInBytes, Lights.ToArray(),
                    BufferUsageHint.StaticDraw);
                GL.EnableVertexAttribArray(4);
                GL.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, 0, 0);
            }
            else
            {
                //0 positions
                GL.BindBuffer(BufferTarget.ArrayBuffer, BufferIds[0]);
                GL.BufferData(BufferTarget.ArrayBuffer, Positions.Count * Vector3.SizeInBytes, Positions.ToArray(),
                    BufferUsageHint.StaticDraw);
                //1 texCoords
                GL.BindBuffer(BufferTarget.ArrayBuffer, BufferIds[1]);
                GL.BufferData(BufferTarget.ArrayBuffer, TexCoords.Count * Vector4.SizeInBytes, TexCoords.ToArray(),
                    BufferUsageHint.StaticDraw);
                //2 normals
                GL.BindBuffer(BufferTarget.ArrayBuffer, BufferIds[2]);
                GL.BufferData(BufferTarget.ArrayBuffer, Normals.Count * Vector4.SizeInBytes, Normals.ToArray(),
                    BufferUsageHint.StaticDraw);
                //3 color
                GL.BindBuffer(BufferTarget.ArrayBuffer, BufferIds[3]);
                GL.BufferData(BufferTarget.ArrayBuffer, Colors.Count * Vector3.SizeInBytes, Colors.ToArray(),
                    BufferUsageHint.StaticDraw);
                //4 light
                GL.BindBuffer(BufferTarget.ArrayBuffer, BufferIds[4]);
                GL.BufferData(BufferTarget.ArrayBuffer, Lights.Count * Vector3.SizeInBytes, Lights.ToArray(),
                    BufferUsageHint.StaticDraw);
            }

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, IndicesId);
            GL.BufferData(BufferTarget.ElementArrayBuffer, Indices.Count * sizeof(uint), Indices.ToArray(),
                BufferUsageHint.StaticDraw);

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
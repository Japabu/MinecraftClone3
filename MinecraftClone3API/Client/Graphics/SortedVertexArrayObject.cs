using System;
using System.Collections.Generic;
using MinecraftClone3API.Entities;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public class SortedVertexArrayObject : VertexArrayObject
    {
        private class FaceInfo : IComparable<FaceInfo>
        {
            private readonly Vector3 _position;

            public readonly uint[] Indices;

            public FaceInfo(Vector3 position, uint[] indices)
            {
                _position = position;
                Indices = indices;
            }

            public int CompareTo(FaceInfo other)
            {
                var cameraPos = PlayerController.Camera.Position;
                return (int) ((cameraPos - other._position).LengthSquared * 10000 - (cameraPos - _position).LengthSquared * 10000);
            }
        }

        private List<FaceInfo> _faceInfos = new List<FaceInfo>(1024);
        private FaceInfo[] _uploadedFaces;

        public override void AddFace(uint[] indices, Vector3 faceMiddle)
        {
            if(_faceInfos == null)
                _faceInfos = new List<FaceInfo>();

            _faceInfos.Add(new FaceInfo(faceMiddle, indices));
        }

        public override void Upload()
        {
            if (_faceInfos == null || _faceInfos.Count <= 0)
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

            UploadedCount = _faceInfos.Count*6;
            _uploadedFaces = _faceInfos.ToArray();

            var indices = new List<uint>();
            foreach (var face in _uploadedFaces)
                indices.AddRange(face.Indices);
            
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, IndicesId);
            GL.BufferData(BufferTarget.ElementArrayBuffer, UploadedCount * sizeof(uint), indices.ToArray(), BufferUsageHint.DynamicDraw);
        }

        public override void Clear()
        {
            base.Clear();

            _faceInfos = null;
        }

        public override void Sort()
        {
            Array.Sort(_uploadedFaces);

            var sortedIndices = new List<uint>();
            foreach (var face in _uploadedFaces)
                sortedIndices.AddRange(face.Indices);

            //TODO: Use streaming BufferSubData too slow
            GL.BindVertexArray(VaoId);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, IndicesId);
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, (IntPtr)(sortedIndices.Count * sizeof(uint)), sortedIndices.ToArray());
        }
    }
}
using MinecraftClone3API.Client;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public class BoundingBoxRenderer
    {
        private static readonly Vector3[] Vertices =
        {
            //North
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(+0.5f, -0.5f, -0.5f),
            new Vector3(-0.5f, +0.5f, -0.5f), new Vector3(+0.5f, +0.5f, -0.5f),
            //South
            new Vector3(+0.5f, -0.5f, +0.5f), new Vector3(-0.5f, -0.5f, +0.5f),
            new Vector3(+0.5f, +0.5f, +0.5f), new Vector3(-0.5f, +0.5f, +0.5f),
        };

        private static readonly uint[] Indices =
        {
            0, 1, 1, 3, 3, 2, 2, 0,
            4, 5, 5, 7, 7, 6, 6, 4,
            0, 5, 1, 4, 2, 7, 3, 6
        };

        private static VertexArrayObject _vbo;

        public static void Load()
        {
            _vbo = new VertexArrayObject();
            foreach (var vertex in Vertices)
                _vbo.Add(vertex, Vector4.Zero, Vector4.Zero, Vector3.Zero, Vector3.Zero);
            _vbo.AddFace(Indices, Vector3.Zero);
            _vbo.Upload();
        }

        public static void Render(AxisAlignedBoundingBox boundingBox, Vector3 translation, float scale, Camera camera, Matrix4 projection)
        {
            var transform = Matrix4.CreateScale(boundingBox.Scale * scale) * Matrix4.CreateTranslation(boundingBox.Translation + translation) *
                        camera.View * projection;

            var shader = ClientResources.BlockOutlineShader;
            shader.Bind();
            GL.UniformMatrix4(shader.GetUniformLocation("uTransform"), false, ref transform);
            GL.Uniform4(shader.GetUniformLocation("uColor"), Color4.Black);

            _vbo.Draw(BeginMode.Lines);
        }
    }
}

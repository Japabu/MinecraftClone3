using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Draws remote entities as simple solid placeholder cubes using the block-outline shader, whose
    /// fragment stage flags the pixels as unlit so the flat <c>uColor</c> survives composition.
    /// </summary>
    public static class EntityRenderer
    {
        private static readonly Vector3 CubeSize = new Vector3(0.6f, 1.8f, 0.6f);
        private static readonly Color4 CubeColor = new Color4(0.85f, 0.25f, 0.25f, 1f);

        private static readonly Vector3[] Vertices =
        {
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(+0.5f, -0.5f, -0.5f),
            new Vector3(+0.5f, +0.5f, -0.5f), new Vector3(-0.5f, +0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f, +0.5f), new Vector3(+0.5f, -0.5f, +0.5f),
            new Vector3(+0.5f, +0.5f, +0.5f), new Vector3(-0.5f, +0.5f, +0.5f)
        };

        private static readonly uint[] Indices =
        {
            0, 2, 1, 0, 3, 2, // back
            4, 5, 6, 4, 6, 7, // front
            0, 1, 5, 0, 5, 4, // bottom
            3, 7, 6, 3, 6, 2, // top
            0, 4, 7, 0, 7, 3, // left
            1, 2, 6, 1, 6, 5  // right
        };

        private static VertexArrayObject _vao;

        public static void Load()
        {
            _vao = new VertexArrayObject();
            foreach (var vertex in Vertices)
                _vao.Add(vertex, Vector4.Zero, Vector4.Zero, Vector3.Zero, Vector3.Zero);
            _vao.AddFace(Indices, Vector3.Zero);
            _vao.Upload();
        }

        public static void Render(WorldClient world, Camera camera, Matrix4 projection)
        {
            var shader = ClientResources.BlockOutlineShader;
            shader.Bind();
            var uTransform = shader.GetUniformLocation("uTransform");
            GL.Uniform4(shader.GetUniformLocation("uColor"), CubeColor);

            foreach (var entity in world.Entities.Values)
            {
                var transform = Matrix4.CreateScale(CubeSize) *
                                Matrix4.CreateTranslation(entity.Position) *
                                camera.View * projection;
                GL.UniformMatrix4(uTransform, false, ref transform);
                _vao.Draw();
            }
        }
    }
}

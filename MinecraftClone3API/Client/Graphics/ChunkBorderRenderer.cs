using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Debug overlay (toggled with F4) drawing wireframe boxes around the chunks near the player so
    /// chunk boundaries are visible. The player's current chunk is highlighted. Uses the block-outline
    /// shader (unlit) and is drawn in the geometry pass, so it depth-tests against the terrain.
    /// </summary>
    public static class ChunkBorderRenderer
    {
        public static bool Enabled;

        private const int RadiusXZ = 2;
        private const int RadiusY = 1;

        private static readonly Color4 CurrentColor = new Color4(1f, 0.3f, 0.3f, 1f);
        private static readonly Color4 NeighbourColor = new Color4(1f, 1f, 0.2f, 1f);

        // Unit cube spanning 0..1 so a scale of Chunk.Size + translation by chunkPos*Size maps
        // exactly onto a chunk's world extent.
        private static readonly Vector3[] Vertices =
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1)
        };

        private static readonly uint[] Indices =
        {
            0, 1, 1, 2, 2, 3, 3, 0,
            4, 5, 5, 6, 6, 7, 7, 4,
            0, 4, 1, 5, 2, 6, 3, 7
        };

        private static VertexArrayObject _vao;

        public static void Load()
        {
            _vao = new VertexArrayObject();
            foreach (var vertex in Vertices)
                _vao.Add(vertex, Vector4.Zero, Vector4.Zero, Vector3.Zero, Vector4.Zero);
            _vao.AddFace(Indices, Vector3.Zero);
            _vao.Upload();
        }

        public static void Render(Camera camera, Matrix4 projection)
        {
            if (!Enabled) return;

            var playerChunk = WorldBase.ChunkInWorld(camera.Position.ToVector3i());

            var shader = ClientResources.BlockOutlineShader;
            shader.Bind();
            var uTransform = shader.GetUniformLocation("uTransform");
            var uColor = shader.GetUniformLocation("uColor");

            for (var dx = -RadiusXZ; dx <= RadiusXZ; dx++)
            for (var dy = -RadiusY; dy <= RadiusY; dy++)
            for (var dz = -RadiusXZ; dz <= RadiusXZ; dz++)
            {
                var chunkPos = playerChunk + new Vector3i(dx, dy, dz);
                var isCurrent = dx == 0 && dy == 0 && dz == 0;

                var transform = Matrix4.CreateScale(Chunk.Size) *
                                Matrix4.CreateTranslation((chunkPos * Chunk.Size).ToVector3()) *
                                camera.View * projection;

                GL.UniformMatrix4(uTransform, false, ref transform);
                GL.Uniform4(uColor, isCurrent ? CurrentColor : NeighbourColor);
                _vao.Draw(BeginMode.Lines);
            }
        }
    }
}

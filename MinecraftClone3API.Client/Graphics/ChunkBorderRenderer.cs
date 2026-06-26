using MinecraftClone3API.Blocks;
using MinecraftClone3API.Graphics.Rhi;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Debug overlay (toggled with F4) drawing wireframe boxes around the chunks near the player so
    /// chunk boundaries are visible. The player's current chunk is highlighted. Draws the shared
    /// <see cref="OutlineRenderer"/> cube into the geometry pass, so it depth-tests against the terrain.
    /// </summary>
    public static class ChunkBorderRenderer
    {
        public static bool Enabled;

        private const int RadiusXZ = 2;
        private const int RadiusY = 1;

        private static readonly Vector4 CurrentColor = new Vector4(1f, 0.3f, 0.3f, 1f);
        private static readonly Vector4 NeighbourColor = new Vector4(1f, 1f, 0.2f, 1f);

        public static void Load() => OutlineRenderer.Load();

        public static void Render(RenderPass pass, Camera camera)
        {
            if (!Enabled) return;

            var playerChunk = WorldBase.ChunkInWorld(camera.Position.ToVector3i());
            var half = Chunk.Size * 0.5f;

            for (var dx = -RadiusXZ; dx <= RadiusXZ; dx++)
            for (var dy = -RadiusY; dy <= RadiusY; dy++)
            for (var dz = -RadiusXZ; dz <= RadiusXZ; dz++)
            {
                var chunkPos = playerChunk + new Vector3i(dx, dy, dz);
                var isCurrent = dx == 0 && dy == 0 && dz == 0;

                // The shared outline cube is centred on the origin, so place it at the chunk's centre.
                var centre = (chunkPos * Chunk.Size).ToVector3() + new Vector3(half, half, half);
                var mvp = Matrix4X4.CreateScale((float)Chunk.Size) *
                          Matrix4X4.CreateTranslation(centre) *
                          Renderer.View * Renderer.Projection;

                OutlineRenderer.Draw(pass, mvp, isCurrent ? CurrentColor : NeighbourColor);
            }
        }
    }
}

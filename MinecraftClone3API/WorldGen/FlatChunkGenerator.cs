using MinecraftClone3API.Blocks;
using Silk.NET.Maths;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// Degenerate fallback used only when no dimension is registered (e.g. the Vanilla plugin is missing).
    /// Produces empty, fully sky-lit chunks so the engine still runs and the missing-content state is
    /// obvious (the player spawns in an empty void) rather than crashing.
    /// </summary>
    public class FlatChunkGenerator : IChunkGenerator
    {
        public int MinChunkY => -1;
        public int MaxChunkY => 1;

        public void Generate(CachedChunk chunk, Vector3D<int> chunkPos)
        {
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            for (var y = 0; y < Chunk.Size; y++)
                chunk.SetSkyLight(x, y, z, LightLevelSkyMax);
        }

        public long GetLodColumn(int wx, int wz) => 0;   // void world has no LOD surface

        public void DecorateLodRegion(Vector3D<int> regionKey, long[] columns) { }

        public Vector3D<int> Spawn() => new Vector3D<int>(0, 4, 0);

        private const int LightLevelSkyMax = 15;
    }
}

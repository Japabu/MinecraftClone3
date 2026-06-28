using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// <see cref="IChunkGenRegion"/> backed by the one chunk being generated plus its generator's pure
    /// heightmap. Block reads/writes are translated to chunk-local coordinates; anything outside the
    /// chunk reads as air and is dropped on write, which is exactly what lets a feature emit in absolute
    /// coordinates and have each overlapping chunk keep only its own slice.
    /// </summary>
    internal class ChunkGenRegion : IChunkGenRegion
    {
        private readonly NoiseChunkGenerator _generator;
        private readonly CachedChunk _chunk;
        private readonly Vector3D<int> _min;

        public ChunkGenRegion(NoiseChunkGenerator generator, CachedChunk chunk, Vector3D<int> chunkPos)
        {
            _generator = generator;
            _chunk = chunk;
            _min = chunkPos * Chunk.Size;
        }

        public int SeaLevel => _generator.SeaLevel;

        public int SurfaceHeight(int worldX, int worldZ) => _generator.SurfaceHeight(worldX, worldZ);

        public Biome BiomeAt(int worldX, int worldZ) => _generator.BiomeAt(worldX, worldZ);

        public Block GetBlock(int worldX, int worldY, int worldZ)
        {
            var lx = worldX - _min.X;
            var ly = worldY - _min.Y;
            var lz = worldZ - _min.Z;
            if (lx < 0 || lx >= Chunk.Size || ly < 0 || ly >= Chunk.Size || lz < 0 || lz >= Chunk.Size)
                return BlockRegistry.BlockAir;

            return _chunk.GetBlock(lx, ly, lz);
        }

        public void SetBlock(int worldX, int worldY, int worldZ, Block block)
        {
            var lx = worldX - _min.X;
            var ly = worldY - _min.Y;
            var lz = worldZ - _min.Z;
            if (lx < 0 || lx >= Chunk.Size || ly < 0 || ly >= Chunk.Size || lz < 0 || lz >= Chunk.Size)
                return;

            _chunk.SetBlock(lx, ly, lz, block);
        }
    }
}

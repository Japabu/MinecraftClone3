using MinecraftClone3API.Blocks;
using Silk.NET.Maths;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// Produces chunk contents for one world. Owns all seeded noise; <see cref="Generate"/> fills a
    /// <see cref="CachedChunk"/> for the given chunk position (the server never meshes, so this is GPU-free
    /// and runs on the load thread). <see cref="MinChunkY"/>/<see cref="MaxChunkY"/> bound the vertical
    /// band the world streams.
    /// </summary>
    public interface IChunkGenerator
    {
        int MinChunkY { get; }
        int MaxChunkY { get; }

        void Generate(CachedChunk chunk, Vector3D<int> chunkPos);

        /// <summary>A cheap surface-only LOD column (packed <see cref="LodColumn"/>: block id + surface Y + sky)
        /// for the distant horizon, with NO full-chunk generation. Called off the server LOD thread, so it MUST
        /// be pure/thread-safe and must not touch any per-chunk scratch that <see cref="Generate"/> owns.
        /// Returns 0 (an empty column) where there is no LOD surface.</summary>
        long GetLodColumn(int wx, int wz);

        /// <summary>Stamps decoration (tree canopies) onto a filled LOD region's packed columns, replaying the
        /// vegetation features' RNG so the horizon shows trees at the same positions the real chunks do. Called
        /// on the server LOD thread after <see cref="GetLodColumn"/> filled the base surface; must stay pure
        /// (read-only generator state + the passed array). No-op for generators without decoration.</summary>
        void DecorateLodRegion(Vector3D<int> regionKey, long[] columns);

        Vector3D<int> Spawn();
    }
}

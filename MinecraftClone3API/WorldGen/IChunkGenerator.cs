using MinecraftClone3API.Blocks;
using OpenTK.Mathematics;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// Produces chunk contents for one world. Owns all seeded noise; <see cref="Generate"/> fills a
    /// <see cref="CachedChunk"/> for the given chunk position (the server never meshes, so this is GL-free
    /// and runs on the load thread). <see cref="MinChunkY"/>/<see cref="MaxChunkY"/> bound the vertical
    /// band the world streams.
    /// </summary>
    public interface IChunkGenerator
    {
        int MinChunkY { get; }
        int MaxChunkY { get; }

        void Generate(CachedChunk chunk, Vector3i chunkPos);

        Vector3i Spawn();
    }
}

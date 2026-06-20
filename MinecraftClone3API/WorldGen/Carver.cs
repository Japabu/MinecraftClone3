using MinecraftClone3API.Blocks;
using OpenTK.Mathematics;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// Subtractively carves a freshly filled chunk (caves, ravines). Runs after terrain + surface + water
    /// but before decoration, so ores placed later won't land in carved air. A dimension holds a list of
    /// carvers; plugins may add their own. <paramref name="surfaceHeights"/> is the generator's
    /// just-filled per-column surface (flat <c>x * Chunk.Size + z</c>) — the exact heights the terrain used,
    /// so a carver reading it can never breach the surface skin.
    /// </summary>
    public abstract class Carver
    {
        public abstract void Carve(CachedChunk chunk, Vector3i chunkPos, NoiseChunkGenerator generator,
            int[] surfaceHeights);
    }
}

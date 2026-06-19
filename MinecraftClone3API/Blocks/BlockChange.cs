using OpenTK.Mathematics;

namespace MinecraftClone3API.Blocks
{
    /// <summary>A single authoritative block/light change queued for network flush. <see cref="LocalIndex"/>
    /// is the <see cref="Chunk.Index"/> of the block within <see cref="ChunkPos"/>; <see cref="Light"/>
    /// is the packed <see cref="Util.LightLevel.Binary"/>; <see cref="Sky"/> is the 0..15 sky-light level.</summary>
    public struct BlockChange
    {
        public readonly Vector3i ChunkPos;
        public readonly ushort LocalIndex;
        public readonly ushort BlockId;
        public readonly ushort Light;
        public readonly ushort Sky;

        public BlockChange(Vector3i chunkPos, ushort localIndex, ushort blockId, ushort light, ushort sky)
        {
            ChunkPos = chunkPos;
            LocalIndex = localIndex;
            BlockId = blockId;
            Light = light;
            Sky = sky;
        }
    }
}

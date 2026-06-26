using System.IO;

namespace MinecraftClone3API.Entities
{
    /// <summary>Per-entity state for a <see cref="EntityFallingBlock"/>: the id of the block that is falling, so
    /// the client knows which block mesh to render. Wire-only (entities aren't persisted), carried on the spawn
    /// packet like any other <see cref="EntityData"/>.</summary>
    public class FallingBlockData : EntityData
    {
        public ushort BlockId;

        public override void Serialize(BinaryWriter writer) => writer.Write(BlockId);
        public override void Deserialize(BinaryReader reader) => BlockId = reader.ReadUInt16();
    }
}

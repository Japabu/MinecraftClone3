using System.IO;
using MinecraftClone3API.Entities;

namespace VanillaPlugin.Entities
{
    /// <summary>Per-sheep state — whether it's been sheared, which hides the wool overlay layer. The entity
    /// analog of a <see cref="MinecraftClone3API.Blocks.BlockData"/>, registered with
    /// <c>RegisterEntityData&lt;SheepData&gt;</c> and synced via the spawn/data packets.</summary>
    public class SheepData : EntityData
    {
        public bool Sheared;

        public override bool OverlayVisible => !Sheared;

        public override void Serialize(BinaryWriter writer) => writer.Write(Sheared);
        public override void Deserialize(BinaryReader reader) => Sheared = reader.ReadBoolean();
    }
}

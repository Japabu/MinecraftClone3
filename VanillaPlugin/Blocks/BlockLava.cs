using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// Still lava. Like <see cref="BlockWater"/> it uses a minimal Vanilla cube model over the real
    /// <c>minecraft:block/lava_still</c> texture; it is a pass-through, non-targetable fluid that emits full
    /// block light (it is the Nether's main light source alongside glowstone). Rendered opaque (no tint).
    /// </summary>
    internal class BlockLava : Block
    {
        public BlockLava() : base("Lava")
        {
            MinecraftId = "minecraft:lava";
            Model = ResourceReader.ReadBlockModel("Vanilla/Models/Lava.json");
        }

        public override bool IsFullBlock(WorldBase world, Vector3i blockPos) => false;
        public override bool CanPassThrough(WorldBase world, Vector3i blockPos) => true;
        public override bool CanTarget(WorldBase world, Vector3i blockPos) => false;
        public override bool IsLiquid => true;

        public override LightLevel GetLightLevel(WorldBase world, Vector3i blockPos) => new LightLevel(15, 11, 6);

        public override ConnectionType ConnectsToBlock(WorldBase world, Vector3i blockPos, Vector3i otherBlockPos,
            Block otherBlock) => otherBlock == this ? ConnectionType.Connected : ConnectionType.Undefined;
    }
}

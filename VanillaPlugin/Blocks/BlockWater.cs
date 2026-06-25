using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// Still water. Vanilla ships no cube model for water (it uses a bespoke fluid renderer), so this uses
    /// a minimal Vanilla cube model that references the real <c>minecraft:block/water_still</c> texture.
    /// That texture is a grey tint-mask (vanilla multiplies it by a per-biome water colour); we apply the
    /// vanilla default water blue via the model's tintindex 0. Rendered translucent (the texture carries
    /// the alpha); you can swim/see through it and it isn't targetable. The animated surface / reflections
    /// are a deliberate later step — see CLAUDE.md.
    /// </summary>
    internal class BlockWater : Block
    {
        private static readonly Color4 WaterBlue = new Color4(0.247f, 0.463f, 0.894f, 1f);

        public BlockWater() : base("Water")
        {
            MinecraftId = "minecraft:water";
            Model = ResourceReader.ReadBlockModel("Vanilla/Models/Water.json");
        }

        public override Color4 GetTintColor(WorldBase world, Vector3i blockPos, int tintId)
            => tintId == 0 ? WaterBlue : Color4.White;

        public override bool IsFullBlock(WorldBase world, Vector3i blockPos) => false;
        public override TransparencyType IsTransparent(WorldBase world, Vector3i blockPos) => TransparencyType.Transparent;
        public override RenderMaterial GetRenderMaterial(WorldBase world, Vector3i blockPos) => RenderMaterial.Water;
        public override bool CanPassThrough(WorldBase world, Vector3i blockPos) => true;
        public override bool CanTarget(WorldBase world, Vector3i blockPos) => false;
        public override bool IsLiquid => true;

        public override ConnectionType ConnectsToBlock(WorldBase world, Vector3i blockPos, Vector3i otherBlockPos,
            Block otherBlock) => otherBlock == this ? ConnectionType.Connected : ConnectionType.Undefined;

        public override int OnLightPassThrough(WorldBase world, Vector3i blockPos, int lightLevel, int color)
            => lightLevel - 1;
    }
}

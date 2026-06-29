using MinecraftClone3API.Blocks;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// Still water. Vanilla ships no cube model for water (it uses a bespoke fluid renderer), so this uses
    /// a minimal Vanilla cube model that references the real <c>minecraft:block/water_still</c> texture.
    /// That texture is a grey tint-mask (vanilla multiplies it by a per-biome water colour); we apply the
    /// vanilla default water blue via the model's tintindex 0. Rendered translucent (the texture carries
    /// the alpha); you can swim/see through it and it isn't targetable.
    /// </summary>
    internal class BlockWater : Block
    {
        private static readonly Vector4D<float> WaterBlue = new Vector4D<float>(0.247f, 0.463f, 0.894f, 1f);

        public BlockWater() : base("Water")
        {
            MinecraftId = "minecraft:water";
            ModelPath = "Vanilla/Models/Water.json";
        }

        protected override CreativeTab DefaultCreativeTab => CreativeTab.NaturalBlocks;

        public override Vector4D<float> GetTintColor(WorldBase world, Vector3D<int> blockPos, int tintId)
            => tintId == 0 ? WaterBlue : new Vector4D<float>(1f,1f,1f,1f);

        public override bool IsFullBlock(WorldBase world, Vector3D<int> blockPos) => false;
        public override TransparencyType IsTransparent(WorldBase world, Vector3D<int> blockPos) => TransparencyType.Transparent;
        public override RenderMaterial GetRenderMaterial(WorldBase world, Vector3D<int> blockPos) => RenderMaterial.Water;
        public override bool CanPassThrough(WorldBase world, Vector3D<int> blockPos) => true;
        public override bool CanTarget(WorldBase world, Vector3D<int> blockPos) => false;
        public override bool IsLiquid => true;

        public override ConnectionType ConnectsToBlock(WorldBase world, Vector3D<int> blockPos, Vector3D<int> otherBlockPos,
            Block otherBlock) => otherBlock == this ? ConnectionType.Connected : ConnectionType.Undefined;

        public override int OnLightPassThrough(WorldBase world, Vector3D<int> blockPos, int lightLevel, int color)
            => lightLevel - 1;
    }
}

using MinecraftClone3API.Blocks;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    internal class BlockTorch : Block
    {
        public BlockTorch() : base("Torch")
        {
            MinecraftId = "minecraft:torch";
            ModelPath = "minecraft:block/torch";
            ItemSpriteTexture = "minecraft:block/torch";
        }

        protected override CreativeTab DefaultCreativeTab => CreativeTab.FunctionalBlocks;

        public override bool IsFullBlock(WorldBase world, Vector3D<int> blockPos) => false;
        public override bool CanPassThrough(WorldBase world, Vector3D<int> blockPos) => true;

        /// <summary>Light is computed by the authoritative (possibly headless) server, so it must not
        /// depend on client state such as the keyboard.</summary>
        public override LightLevel GetLightLevel(WorldBase world, Vector3D<int> blockPos) => new LightLevel(15, 11, 11);
    }
}

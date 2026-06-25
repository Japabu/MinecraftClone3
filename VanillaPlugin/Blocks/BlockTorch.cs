using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;

namespace VanillaPlugin.Blocks
{
    internal class BlockTorch : Block
    {
        public BlockTorch() : base("Torch")
        {
            MinecraftId = "minecraft:torch";
            Model = ResourceReader.ReadBlockModel("minecraft:block/torch");
        }

        public override bool IsFullBlock(WorldBase world, Vector3i blockPos) => false;
        public override bool CanPassThrough(WorldBase world, Vector3i blockPos) => true;

        /// <summary>Light is computed by the authoritative (possibly headless) server, so it must not
        /// depend on client state such as the keyboard.</summary>
        public override LightLevel GetLightLevel(WorldBase world, Vector3i blockPos) => new LightLevel(15, 11, 11);
    }
}

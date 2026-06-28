using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    public class BlockGlass : BlockBasic
    {
        public BlockGlass() : base("Glass", "minecraft:block/glass", false)
        {
        }

        public override TransparencyType IsTransparent(WorldBase world, Vector3D<int> blockPos) => TransparencyType.Cutoff;

        public override ConnectionType ConnectsToBlock(WorldBase world, Vector3D<int> blockPos, Vector3D<int> otherBlockPos,
            Block otherBlock) => otherBlock == this ? ConnectionType.Connected : ConnectionType.Undefined;
    }
}

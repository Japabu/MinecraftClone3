using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.Blocks
{
    public class BlockAir : Block
    {
        internal BlockAir() : base("Air")
        {
        }

        public override bool IsVisible(WorldBase world, Vector3D<int> blockPos) => false;
        public override bool IsFullBlock(WorldBase world, Vector3D<int> blockPos) => false;
        public override bool CanPassThrough(WorldBase world, Vector3D<int> blockPos) => true;
        public override bool CanTarget(WorldBase world, Vector3D<int> blockPos) => false;
    }
}

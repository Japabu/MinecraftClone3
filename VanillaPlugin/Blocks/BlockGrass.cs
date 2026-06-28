using System;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    internal class BlockGrass : BlockBasic
    {
        public BlockGrass() : base("Grass", "minecraft:block/grass_block", true)
        {
        }

        protected override CreativeTab DefaultCreativeTab => CreativeTab.NaturalBlocks;

        public override Vector4D<float> GetTintColor(WorldBase world, Vector3D<int> blockPos, int tintId)
            => tintId == 0 ? GetCurrentColor(blockPos) : new Vector4D<float>(1f, 1f, 1f, 1f);
        private Vector4D<float> GetCurrentColor(Vector3D<int> blockPos)
        {
            return new Vector4D<float>((float) Math.Sin(blockPos.X * 0.02f)*0.5f+0.5f, (float) Math.Cos(blockPos.Z * 0.02f) * 0.5f + 0.5f,
                (float) (Math.Sin(blockPos.X * 0.02f) * (float) Math.Cos(blockPos.Z * 0.02f)) * 0.5f + 0.5f, 1);
        }
    }
}

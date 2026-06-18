using System;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace VanillaPlugin.Blocks
{
    internal class BlockGrass : BlockBasic
    {
        public BlockGrass() : base("Grass", "Vanilla/Models/GrassNormal.json", true)
        {
        }

        public override Color4 GetTintColor(WorldBase world, Vector3i blockPos, int tintId)
            => tintId == 0 ? GetCurrentColor(blockPos) : Color4.White;
        private Color4 GetCurrentColor(Vector3i blockPos)
        {
            return new Color4((float) Math.Sin(blockPos.X * 0.02f)*0.5f+0.5f, (float) Math.Cos(blockPos.Z * 0.02f) * 0.5f + 0.5f,
                (float) (Math.Sin(blockPos.X * 0.02f) * (float) Math.Cos(blockPos.Z * 0.02f)) * 0.5f + 0.5f, 1);
        }
    }
}

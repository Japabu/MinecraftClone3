using System;
using MinecraftClone3API.Blocks;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// A <see cref="BlockPlant"/> whose <c>tinted_cross</c> model is biome-tinted (short grass, fern): face
    /// tint index 0 is coloured the same way <see cref="BlockGrass"/> colours grass blocks, so a grass tuft
    /// blends into the ground it stands on.
    /// </summary>
    public class BlockTintedPlant : BlockPlant
    {
        public BlockTintedPlant(string name, string minecraftId) : base(name, minecraftId)
        {
        }

        public override Vector4D<float> GetTintColor(WorldBase world, Vector3D<int> blockPos, int tintId)
            => tintId == 0
                ? new Vector4D<float>((float) Math.Sin(blockPos.X * 0.02f) * 0.5f + 0.5f,
                    (float) Math.Cos(blockPos.Z * 0.02f) * 0.5f + 0.5f,
                    (float) (Math.Sin(blockPos.X * 0.02f) * Math.Cos(blockPos.Z * 0.02f)) * 0.5f + 0.5f, 1f)
                : new Vector4D<float>(1f, 1f, 1f, 1f);
    }
}

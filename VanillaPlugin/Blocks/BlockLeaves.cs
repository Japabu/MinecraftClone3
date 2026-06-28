using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    /// <summary>Leaves: a cutout cube (the vanilla leaves texture has transparent gaps) tinted green via the
    /// model's tintindex 0. One instance per wood type, differing only in name and model.</summary>
    internal class BlockLeaves : BlockBasic
    {
        private static readonly Vector4D<float> LeafGreen = new Vector4D<float>(0.34f, 0.55f, 0.24f, 1f);

        public BlockLeaves(string name, string modelPath) : base(name, modelPath, true, 0.2f)
        {
        }

        public override TransparencyType IsTransparent(WorldBase world, Vector3D<int> blockPos) => TransparencyType.Cutoff;

        public override Vector4D<float> GetTintColor(WorldBase world, Vector3D<int> blockPos, int tintId)
            => tintId == 0 ? LeafGreen : new Vector4D<float>(1f, 1f, 1f, 1f);
    }
}

using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace VanillaPlugin.Blocks
{
    /// <summary>Oak leaves: a cutout cube (the vanilla leaves texture has transparent gaps) tinted green
    /// via the model's tintindex 0.</summary>
    internal class BlockLeaves : BlockBasic
    {
        private static readonly Color4 LeafGreen = new Color4(0.34f, 0.55f, 0.24f, 1f);

        public BlockLeaves() : base("Leaves", "minecraft:block/oak_leaves", true)
        {
        }

        public override TransparencyType IsTransparent(WorldBase world, Vector3i blockPos) => TransparencyType.Cutoff;

        public override Color4 GetTintColor(WorldBase world, Vector3i blockPos, int tintId)
            => tintId == 0 ? LeafGreen : Color4.White;
    }
}

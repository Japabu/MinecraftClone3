using System.Text;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    /// <summary>A coloured stained-glass block. Colour is the block's identity (one block per Minecraft dye
    /// colour) rather than placement metadata, matching vanilla — each pulls its model/texture straight from
    /// the pack (<c>minecraft:block/&lt;colour&gt;_stained_glass</c>). See-through (Transparent) and connects
    /// only to glass of the same colour.</summary>
    public class BlockStainedGlass : BlockBasic
    {
        public BlockStainedGlass(string color)
            : base(ToPascalCase(color) + "StainedGlass", "minecraft:block/" + color + "_stained_glass", false)
        {
        }

        protected override CreativeTab DefaultCreativeTab => CreativeTab.ColoredBlocks;

        public override TransparencyType IsTransparent(WorldBase world, Vector3D<int> blockPos) => TransparencyType.Transparent;

        public override ConnectionType ConnectsToBlock(WorldBase world, Vector3D<int> blockPos, Vector3D<int> otherBlockPos,
            Block otherBlock) => otherBlock == this ? ConnectionType.Connected : ConnectionType.Undefined;

        private static string ToPascalCase(string snake)
        {
            var sb = new StringBuilder();
            foreach (var part in snake.Split('_'))
                if (part.Length > 0) sb.Append(char.ToUpperInvariant(part[0])).Append(part.Substring(1));
            return sb.ToString();
        }
    }
}

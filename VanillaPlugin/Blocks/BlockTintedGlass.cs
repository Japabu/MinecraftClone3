using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VanillaPlugin.BlockDatas;

namespace VanillaPlugin.Blocks
{
    public class BlockTintedGlass : Block
    {
        private static readonly Keys[] BindKeys = {Keys.U, Keys.I, Keys.O, Keys.P};
        private static readonly string[] TextureNames =
        {
            "minecraft:block/black_stained_glass",
            "minecraft:block/blue_stained_glass",
            "minecraft:block/green_stained_glass",
            "minecraft:block/magenta_stained_glass"
        };

        private static readonly int[,] LightFilters =
        {
            {4, 4, 4},
            {10, 10, 1 },
            {10, 1, 10 },
            {1, 10, 10 }
        };

        private static BlockTexture[] _textures;

        public BlockTintedGlass() : base("TintedGlass")
        {
            _textures = new BlockTexture[TextureNames.Length];
            for (var i = 0; i < TextureNames.Length; i++)
            {
                _textures[i] = ResourceReader.ReadBlockTexture(TextureNames[i]);
            }
        }

        public override TransparencyType IsTransparent(WorldBase world, Vector3i blockPos) => TransparencyType.Transparent;
        public override ConnectionType ConnectsToBlock(WorldBase world, Vector3i blockPos, Vector3i otherBlockPos,
            Block otherBlock)
        {
            var data = world.GetBlockData(blockPos) as BlockDataMetadata;
            var myMeta = data?.Metadata ?? 0;

            data = world.GetBlockData(otherBlockPos) as BlockDataMetadata;
            var otherMeta = data?.Metadata ?? 0;

            return otherBlock == this && myMeta == otherMeta ? ConnectionType.Connected : ConnectionType.Undefined;
        }

        public override int GetPlacementMetadata(KeyboardState ks)
        {
            for (var i = 0; i < BindKeys.Length; i++)
            {
                if (ks.IsKeyDown(BindKeys[i])) return i;
            }

            return 0;
        }

        public override void OnPlaced(WorldBase world, Vector3i blockPos, EntityPlayer player, int metadata)
        {
            world.SetBlockData(blockPos, new BlockDataMetadata(metadata));
        }

        public override int OnLightPassThrough(WorldBase world, Vector3i blockPos, int lightLevel, int color)
        {
            var data = world.GetBlockData(blockPos) as BlockDataMetadata;
            var i = data?.Metadata ?? 0;
            return lightLevel - LightFilters[i, color];
        }
    }
}

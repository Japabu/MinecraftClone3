using MinecraftClone3API.Blocks;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// A small decorative plant rendered as a Minecraft <c>cross</c> model (two 45°-rotated planes forming an
    /// X): flowers, ferns, grass tufts. Pass-through (no collision), alpha-tested (cutout), breaks instantly,
    /// and can only sit on a grass/dirt top (<see cref="CanPlaceAt"/>). The cross silhouette relies on the
    /// mesher's element-rotation support. The block is stateless — its look comes straight from its
    /// <c>ModelPath</c> cross model in the resource pack.
    /// </summary>
    public class BlockPlant : Block
    {
        private static readonly Vector3D<int> Down = new Vector3D<int>(0, -1, 0);

        public BlockPlant(string name, string minecraftId) : base(name)
        {
            MinecraftId = minecraftId;
            ModelPath = minecraftId.Replace("minecraft:", "minecraft:block/");
            // The flat item icon is the cross texture rendered as a 2D sprite (like vanilla item/generated). This
            // reuses ModelPath as the texture location, which is valid only because every registered plant's cross
            // model shares its texture's name (block/dandelion model -> block/dandelion texture). A future plant
            // whose model name differs from its texture (e.g. tall_grass uses block/grass) must set this explicitly.
            ItemSpriteTexture = ModelPath;
        }

        protected override CreativeTab DefaultCreativeTab => CreativeTab.NaturalBlocks;

        public override bool IsFullBlock(WorldBase world, Vector3D<int> blockPos) => false;
        public override bool CanPassThrough(WorldBase world, Vector3D<int> blockPos) => true;
        public override TransparencyType IsTransparent(WorldBase world, Vector3D<int> blockPos) => TransparencyType.Cutoff;

        public override float Hardness => 0f;

        // Light passes through a plant exactly like air (the default −1 per step) — a thin plane doesn't block it,
        // but it must still attenuate with distance or light would leak undimmed along a row of plants.

        /// <summary>Plants need a grass or dirt top to stand on (matches vanilla's plantable soils for the
        /// classic flowers and grass).</summary>
        public override bool CanPlaceAt(WorldBase world, Vector3D<int> blockPos, int metadata)
        {
            var below = world.GetBlock(blockPos + Down).RegistryKey;
            return below == "Vanilla:Grass" || below == "Vanilla:Dirt";
        }
    }
}

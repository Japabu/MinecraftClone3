using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// A void <see cref="WorldBase"/> used only to mesh a single block into an inventory icon
    /// (see <see cref="ItemIconRenderer"/>): every neighbour reads as air so all six faces survive
    /// culling, and light reads as full so the mesher never darkens the icon (the ItemIcon shader
    /// forward-shades with a fixed per-face brightness anyway). Holds no chunks; never mutated.
    /// </summary>
    public sealed class IconWorld : WorldBase
    {
        public static readonly IconWorld Instance = new IconWorld();

        private static readonly LightLevel Full = new LightLevel(LightLevel.Max, LightLevel.Max, LightLevel.Max);

        public override Block GetBlock(int x, int y, int z) => BlockRegistry.BlockAir;
        public override BlockData GetBlockData(int x, int y, int z) => null;
        public override LightLevel GetBlockLightLevel(int x, int y, int z) => Full;
        public override int GetSkyLight(int x, int y, int z) => LightLevel.SkyMax;

        public override void SetBlock(int x, int y, int z, Block block, bool update, bool lowPriority) { }
        public override void SetBlockData(int x, int y, int z, BlockData data) { }
        public override void SetBlockLightLevel(int x, int y, int z, LightLevel lightLevel) { }
        public override void SetSkyLight(int x, int y, int z, int level) { }
        public override void PlaceBlock(EntityPlayer player, Vector3i blockPos, Block block, int metadata) { }
        public override void Update() { }
    }
}

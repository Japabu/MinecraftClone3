using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// The active portal surface inside an obsidian frame: a non-solid, pass-through, non-targetable block
    /// that emits a purple glow. Placed by the engine when a frame is lit with flint &amp; steel
    /// (<see cref="VanillaPlugin.Items.ItemFlintAndSteel"/>); standing in it triggers a dimension transfer
    /// (handled server-side). Registry key <c>Vanilla:NetherPortal</c>.
    /// </summary>
    internal class BlockNetherPortal : Block
    {
        public const string Key = "Vanilla:NetherPortal";

        public BlockNetherPortal() : base("NetherPortal")
        {
            MinecraftId = "minecraft:nether_portal";
            Model = ResourceReader.ReadBlockModel("Vanilla/Models/NetherPortal.json");
        }

        public override bool IsFullBlock(WorldBase world, Vector3i blockPos) => false;
        public override TransparencyType IsTransparent(WorldBase world, Vector3i blockPos) => TransparencyType.Cutoff;
        public override bool CanPassThrough(WorldBase world, Vector3i blockPos) => true;
        public override bool CanTarget(WorldBase world, Vector3i blockPos) => false;

        public override LightLevel GetLightLevel(WorldBase world, Vector3i blockPos) => new LightLevel(11, 4, 15);
    }
}

using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using VanillaPlugin.BlockDatas;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// The active portal surface inside an obsidian frame: a non-solid, pass-through, non-targetable block that
    /// emits a purple glow. Renders the real Minecraft thin pane — the pack's <c>blockstates/nether_portal.json</c>
    /// picks <c>nether_portal_ns</c>/<c>_ew</c> from the block's stored axis (0 = X / 1 = Z, in a
    /// <see cref="BlockDataMetadata"/>). Placed by the engine when a frame is lit with flint &amp; steel
    /// (<see cref="VanillaPlugin.Items.ItemFlintAndSteel"/>); standing in it triggers a dimension transfer
    /// (handled server-side). Registry key <c>Vanilla:NetherPortal</c>.
    /// </summary>
    internal class BlockNetherPortal : Block
    {
        public const string Key = "Vanilla:NetherPortal";

        /// <summary>Axis metadata values stored in the block's <see cref="BlockDataMetadata"/>.</summary>
        public const int AxisX = 0;
        public const int AxisZ = 1;

        public BlockNetherPortal() : base("NetherPortal")
        {
            MinecraftId = "minecraft:nether_portal";
            Model = ResourceReader.ReadBlockModel("minecraft:block/nether_portal_ns");
            StateDefinition = ResourceReader.ReadBlockState("minecraft:nether_portal");
        }

        public override bool IsFullBlock(WorldBase world, Vector3i blockPos) => false;
        public override TransparencyType IsTransparent(WorldBase world, Vector3i blockPos) => TransparencyType.Transparent;
        public override RenderMaterial GetRenderMaterial(WorldBase world, Vector3i blockPos) => RenderMaterial.Solid;
        public override bool CanPassThrough(WorldBase world, Vector3i blockPos) => true;
        public override bool CanTarget(WorldBase world, Vector3i blockPos) => false;

        public override LightLevel GetLightLevel(WorldBase world, Vector3i blockPos) => new LightLevel(11, 4, 15);

        public override IReadOnlyDictionary<string, string> GetBlockState(WorldBase world, Vector3i blockPos)
        {
            var axis = (world.GetBlockData(blockPos) as BlockDataMetadata)?.Metadata ?? AxisX;
            return new Dictionary<string, string> { { "axis", axis == AxisZ ? "z" : "x" } };
        }
    }
}

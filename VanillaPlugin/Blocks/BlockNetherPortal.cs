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
    /// (server-side). It ticks each server tick and pops once its obsidian frame is broken, cascading to the
    /// connected portal blocks. Registry key <c>Vanilla:NetherPortal</c>.
    /// </summary>
    internal class BlockNetherPortal : Block
    {
        public const string Key = "Vanilla:NetherPortal";

        /// <summary>Axis metadata values stored in the block's <see cref="BlockDataMetadata"/>.</summary>
        public const int AxisX = 0;
        public const int AxisZ = 1;

        private static readonly Vector3i Up = new Vector3i(0, 1, 0);

        private Block _obsidian;
        private Block Obsidian => _obsidian ??= GameRegistry.GetBlock("Vanilla:Obsidian");

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

        public override bool NeedsServerTick => true;

        /// <summary>Pops when the frame around it is gone: clearing this block notifies its neighbours, so a
        /// connected portal collapses ring-by-ring over the following ticks (matching the falling-block cascade).</summary>
        public override void OnServerTick(WorldServer world, Vector3i blockPos)
        {
            if (FrameIntact(world, blockPos)) return;
            world.SetBlock(blockPos, BlockRegistry.BlockAir);
        }

        /// <summary>True while each of the four in-plane neighbours (the two along the portal axis, plus up/down)
        /// is still a portal block or obsidian. An unloaded neighbour counts as intact so the portal doesn't
        /// self-destruct when a frame chunk merely streams out.</summary>
        private bool FrameIntact(WorldServer world, Vector3i pos)
        {
            var axis = (world.GetBlockData(pos) as BlockDataMetadata)?.Metadata ?? AxisX;
            var along = axis == AxisZ ? new Vector3i(0, 0, 1) : new Vector3i(1, 0, 0);
            return Supported(world, pos + along) && Supported(world, pos - along)
                && Supported(world, pos + Up) && Supported(world, pos - Up);
        }

        private bool Supported(WorldServer world, Vector3i p)
        {
            if (world.IsBlockInEmptyChunk(p)) return true;
            var b = world.GetBlock(p.X, p.Y, p.Z);
            return b == this || b == Obsidian;
        }
    }
}

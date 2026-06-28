using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using Silk.NET.Maths;
using VanillaPlugin.BlockDatas;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// The active portal surface inside an obsidian frame: a non-solid, pass-through, non-targetable block that
    /// emits a purple glow. Renders the real Minecraft thin pane — the pack's <c>blockstates/nether_portal.json</c>
    /// picks <c>nether_portal_ns</c>/<c>_ew</c> from the block's stored axis (0 = X / 1 = Z, in a
    /// <see cref="BlockDataMetadata"/>). Placed by the engine when a frame is lit with flint &amp; steel
    /// (<see cref="VanillaPlugin.Items.ItemFlintAndSteel"/>); standing in it triggers a dimension transfer
    /// (server-side). It pops once its obsidian frame is broken — validity is checked only when a neighbour
    /// changes (never polled), cascading to the connected portal blocks. Registry key <c>Vanilla:NetherPortal</c>.
    /// </summary>
    internal class BlockNetherPortal : Block
    {
        public const string Key = "Vanilla:NetherPortal";

        /// <summary>Axis metadata values stored in the block's <see cref="BlockDataMetadata"/>.</summary>
        public const int AxisX = 0;
        public const int AxisZ = 1;

        private static readonly Vector3D<int> Up = new Vector3D<int>(0, 1, 0);

        private static readonly Vector3D<int>[] FaceNeighbors =
        {
            new Vector3D<int>(1, 0, 0), new Vector3D<int>(-1, 0, 0),
            new Vector3D<int>(0, 1, 0), new Vector3D<int>(0, -1, 0),
            new Vector3D<int>(0, 0, 1), new Vector3D<int>(0, 0, -1),
        };

        // True on the tick thread while a collapse is draining: re-entrant OnNeighborChanged calls from the
        // SetBlock notifications become no-ops, so the whole sheet clears in one flat loop, never a deep stack.
        [System.ThreadStatic] private static bool _collapsing;

        private Block _obsidian;
        private Block Obsidian => _obsidian ??= GameRegistry.GetBlock("Vanilla:Obsidian");

        public BlockNetherPortal() : base("NetherPortal")
        {
            MinecraftId = "minecraft:nether_portal";
            ModelPath = "minecraft:block/nether_portal_ns";
            BlockStateId = "minecraft:nether_portal";
        }

        public override bool IsFullBlock(WorldBase world, Vector3D<int> blockPos) => false;
        public override TransparencyType IsTransparent(WorldBase world, Vector3D<int> blockPos) => TransparencyType.Transparent;
        public override RenderMaterial GetRenderMaterial(WorldBase world, Vector3D<int> blockPos) => RenderMaterial.Solid;
        public override bool CanPassThrough(WorldBase world, Vector3D<int> blockPos) => true;
        public override bool CanTarget(WorldBase world, Vector3D<int> blockPos) => false;

        public override LightLevel GetLightLevel(WorldBase world, Vector3D<int> blockPos) => new LightLevel(11, 4, 15);

        public override IReadOnlyDictionary<string, string> GetBlockState(WorldBase world, Vector3D<int> blockPos)
        {
            var axis = (world.GetBlockData(blockPos) as BlockDataMetadata)?.Metadata ?? AxisX;
            return new Dictionary<string, string> { { "axis", axis == AxisZ ? "z" : "x" } };
        }

        /// <summary>Re-validates only when a neighbour actually changes — no per-tick polling. A neighbour that
        /// became part of the frame is ignored (so lighting the portal cell-by-cell doesn't read a half-built
        /// frame as broken); a neighbour that stopped being frame triggers the check, and clearing this block
        /// cascades it to the rest of the portal, so breaking one frame block collapses the whole sheet at once.</summary>
        public override void OnNeighborChanged(WorldServer world, Vector3D<int> blockPos, Vector3D<int> changedPos)
        {
            if (_collapsing) return;
            if (Supported(world, changedPos)) return;
            if (!FrameIntact(world, blockPos)) Collapse(world, blockPos);
        }

        /// <summary>Clears the whole connected sheet of portal blocks in one breadth-first pass. Clearing a block
        /// still notifies its neighbours, but the <see cref="_collapsing"/> guard makes those re-entrant
        /// <see cref="OnNeighborChanged"/> calls no-ops — so a large portal collapses as a flat loop instead of a
        /// per-block recursion that could overflow the tick thread's stack.</summary>
        private void Collapse(WorldServer world, Vector3D<int> start)
        {
            _collapsing = true;
            try
            {
                var frontier = new Queue<Vector3D<int>>();
                frontier.Enqueue(start);
                while (frontier.Count > 0)
                {
                    var p = frontier.Dequeue();
                    if (world.GetBlock(p.X, p.Y, p.Z) != this) continue;
                    world.SetBlock(p, BlockRegistry.BlockAir);
                    foreach (var off in FaceNeighbors)
                    {
                        var n = p + off;
                        if (world.GetBlock(n.X, n.Y, n.Z) == this) frontier.Enqueue(n);
                    }
                }
            }
            finally { _collapsing = false; }
        }

        /// <summary>True while each of the four in-plane neighbours (the two along the portal axis, plus up/down)
        /// is still a portal block or obsidian. An unloaded neighbour counts as intact so the portal doesn't
        /// self-destruct when a frame chunk merely streams out.</summary>
        private bool FrameIntact(WorldServer world, Vector3D<int> pos)
        {
            var axis = (world.GetBlockData(pos) as BlockDataMetadata)?.Metadata ?? AxisX;
            var along = axis == AxisZ ? new Vector3D<int>(0, 0, 1) : new Vector3D<int>(1, 0, 0);
            return Supported(world, pos + along) && Supported(world, pos - along)
                && Supported(world, pos + Up) && Supported(world, pos - Up);
        }

        private bool Supported(WorldServer world, Vector3D<int> p)
        {
            if (world.IsBlockInEmptyChunk(p)) return true;
            var b = world.GetBlock(p.X, p.Y, p.Z);
            return b == this || b == Obsidian;
        }
    }
}

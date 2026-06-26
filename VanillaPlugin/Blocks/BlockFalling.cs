using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// A block affected by gravity (sand, gravel): when the cell beneath it is empty it descends one block per
    /// tick until it lands on a solid block. Falling is server-authoritative — the block simply moves through
    /// the grid (no falling-block entity). It ticks only while unsupported: a freshly placed block ticks via
    /// the <see cref="Block.NeedsServerTick"/> registration in <c>SetBlock</c>, a resting block starts again
    /// when <see cref="OnNeighborChanged"/> sees its support removed, and it deregisters itself on landing.
    /// </summary>
    public class BlockFalling : BlockBasic
    {
        private static readonly Vector3i Down = new Vector3i(0, -1, 0);

        public BlockFalling(string name, string modelPath) : base(name, modelPath, true)
        {
        }

        public override bool NeedsServerTick => true;

        public override void OnNeighborChanged(WorldServer world, Vector3i blockPos, Vector3i changedPos)
        {
            if (changedPos == blockPos + Down) world.ScheduleBlockTick(blockPos);
        }

        public override void OnServerTick(WorldServer world, Vector3i blockPos)
        {
            var below = blockPos + Down;

            // Don't fall into an unloaded column — that would force-create a chunk and drop the block into the
            // void. Treat the edge of the loaded world as solid ground.
            if (world.IsBlockInEmptyChunk(below) || world.GetBlock(below) != BlockRegistry.BlockAir)
            {
                world.UnscheduleBlockTick(blockPos);
                return;
            }

            world.SetBlock(blockPos, BlockRegistry.BlockAir);
            world.SetBlock(below, this);
        }
    }
}

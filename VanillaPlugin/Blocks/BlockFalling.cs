using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// A block affected by gravity (sand, gravel): when the cell beneath it is empty it clears itself and spawns
    /// a <see cref="EntityFallingBlock"/> that falls smoothly under gravity and turns back into a block on
    /// landing. Server-authoritative. It is armed for a tick only when it might be unsupported: a freshly placed
    /// block via the <see cref="Block.NeedsServerTick"/> registration in <c>SetBlock</c>, a resting block when
    /// <see cref="OnNeighborChanged"/> sees the block beneath it removed.
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

            // Don't fall into an unloaded column — that would let the entity drop through ungenerated terrain.
            // Treat the edge of the loaded world as solid ground.
            if (world.IsBlockInEmptyChunk(below) || world.GetBlock(below) != BlockRegistry.BlockAir) return;

            // Clear the cell (this also deregisters it from ticking) and hand the fall off to an entity. Clearing
            // notifies the block above, so a stacked column converts to falling entities one tick after another.
            world.SetBlock(blockPos, BlockRegistry.BlockAir);
            world.SpawnFallingBlock(Id, blockPos);
        }
    }
}

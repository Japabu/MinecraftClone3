using System;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Entities
{
    /// <summary>
    /// A block falling under gravity (spawned by a <c>BlockFalling</c> when its support is removed). Server-side
    /// it falls with <see cref="EntityPhysics"/> until it lands, then turns back into a placed block at the cell
    /// it came to rest in (stacking one cell up if that cell was filled in the meantime, e.g. by the block below
    /// it in the same falling column). Clients render it as a full-size block at its interpolated position.
    /// <see cref="Entity.Position"/> is the block's bottom-centre (the physics "feet").
    /// </summary>
    public class EntityFallingBlock : Entity
    {
        // Just under a full block so it slips cleanly between/onto neighbouring blocks, like Minecraft's.
        public const float Size = 0.98f;

        /// <summary>The block this entity will place when it lands. Mirrored into <see cref="Entity.Data"/> as a
        /// <see cref="FallingBlockData"/> for the wire; set directly on the server, restored from Data on clients.</summary>
        public ushort BlockId;

        public override void Update()
        {
            var world = ServerWorld;
            if (world == null) return;

            EntityPhysics.Tick(world, this, Size, Size);
            if (OnGround) Land(world);
        }

        // Persist the falling block by stable name (not its session-local id), and rebuild the wire-facing
        // FallingBlockData mirror on load so a respawned falling block still renders the right block.
        internal override void SerializeState(System.IO.BinaryWriter writer)
            => writer.Write(GameRegistry.GetBlock(BlockId).RegistryKey);

        internal override void DeserializeState(System.IO.BinaryReader reader)
        {
            BlockId = GameRegistry.BlockRegistry.GetOrRegisterUnknown(reader.ReadString());
            Data = new FallingBlockData {BlockId = BlockId};
        }

        private void Land(WorldServer world)
        {
            Dead = true;

            var block = GameRegistry.GetBlock(BlockId);
            if (block == BlockRegistry.BlockAir) return;

            // The cell whose bottom edge sits where the entity came to rest (a resting block at by leaves the
            // feet at by + 0.5, so the occupied cell is by + 1).
            var cell = new Vector3i(
                (int) MathF.Round(Position.X),
                (int) MathF.Round(Position.Y + 0.5f),
                (int) MathF.Round(Position.Z));

            // Normally the rest cell is empty; if another block from the same column already filled it this tick,
            // settle one cell higher. If both are taken (a tight pocket) the block is simply lost — rare enough
            // not to warrant re-dropping it as an item.
            if (world.GetBlock(cell) != BlockRegistry.BlockAir) cell.Y += 1;
            if (world.GetBlock(cell) == BlockRegistry.BlockAir) world.SetBlock(cell, block);
        }
    }
}

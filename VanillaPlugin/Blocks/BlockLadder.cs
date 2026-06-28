using System;
using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;
using VanillaPlugin.BlockDatas;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// A wall-mounted, climbable ladder. The plane + facing rotation come from the pack's
    /// <c>blockstates/ladder.json</c> (selected by <see cref="GetBlockState"/>); the facing (the open side the
    /// ladder presents to the player, opposite the wall it hangs on) lives in block metadata. It is pass-through
    /// (no collision) but <see cref="IsClimbable"/>, and can only be placed where a solid block backs it
    /// (<see cref="CanPlaceAt"/>). The climb itself lives in <see cref="PlayerPhysics"/>.
    /// </summary>
    public class BlockLadder : Block
    {
        private const int North = 0; // -Z
        private const int East = 1;  // +X
        private const int South = 2; // +Z
        private const int West = 3;  // -X
        private static readonly string[] FacingNames = { "north", "east", "south", "west" };
        private static readonly Vector3D<int>[] FacingNormals =
        {
            new Vector3D<int>(0, 0, -1), new Vector3D<int>(1, 0, 0),
            new Vector3D<int>(0, 0, 1), new Vector3D<int>(-1, 0, 0)
        };

        public BlockLadder() : base("Ladder")
        {
            MinecraftId = "minecraft:ladder";
            ModelPath = "minecraft:block/ladder";
            BlockStateId = "minecraft:ladder";
            ItemSpriteTexture = "minecraft:block/ladder";
        }

        protected override CreativeTab DefaultCreativeTab => CreativeTab.FunctionalBlocks;

        public override bool IsFullBlock(WorldBase world, Vector3D<int> blockPos) => false;
        public override bool CanPassThrough(WorldBase world, Vector3D<int> blockPos) => true;
        public override bool IsClimbable(WorldBase world, Vector3D<int> blockPos) => true;
        public override TransparencyType IsTransparent(WorldBase world, Vector3D<int> blockPos) => TransparencyType.Cutoff;

        public override float Hardness => 0.4f;
        public override ToolType PreferredTool => ToolType.Axe;

        public override IReadOnlyDictionary<string, string> GetBlockState(WorldBase world, Vector3D<int> blockPos)
            => new Dictionary<string, string> { { "facing", FacingNames[Facing(world, blockPos)] } };

        /// <summary>The ladder hangs on the block behind it (opposite its facing); without that solid backing it
        /// can't be placed. The facing comes from the placement metadata — the block data isn't written until
        /// OnPlaced, which runs after this check.</summary>
        public override bool CanPlaceAt(WorldBase world, Vector3D<int> blockPos, int metadata)
        {
            var wall = blockPos - FacingNormals[metadata & 0x3];
            return world.GetBlock(wall).IsFullBlock(world, wall);
        }

        public override int GetPlacementMetadata(EntityPlayer player, BlockRaytraceResult ray)
        {
            // Clicking a vertical face hangs the ladder on that block, facing the player (the clicked face's
            // direction). Clicking a top/bottom face has no wall, so fall back to facing the player's look.
            switch (ray?.Face)
            {
                case BlockFace.Back: return North;
                case BlockFace.Right: return East;
                case BlockFace.Front: return South;
                case BlockFace.Left: return West;
                default:
                    var fx = Math.Sin(player.Yaw);
                    var fz = Math.Cos(player.Yaw);
                    if (Math.Abs(fz) >= Math.Abs(fx)) return fz >= 0 ? South : North;
                    return fx >= 0 ? East : West;
            }
        }

        public override void OnPlaced(WorldBase world, Vector3D<int> blockPos, EntityPlayer player, int metadata)
            => world.SetBlockData(blockPos, new BlockDataMetadata(metadata & 0x3));

        private static int Facing(WorldBase world, Vector3D<int> pos)
            => (world.GetBlockData(pos) as BlockDataMetadata)?.Metadata ?? North;
    }
}

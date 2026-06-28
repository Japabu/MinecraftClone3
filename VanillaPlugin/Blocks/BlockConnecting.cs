using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// Shared base for blocks that visually + physically connect to their horizontal neighbours via the pack's
    /// <c>multipart</c> blockstate (fences, walls): a central post plus an arm toward each connected side. The
    /// connection set is derived from the world at mesh/collision time (so it updates when a neighbour changes),
    /// never stored. Subclasses define which neighbours count (<see cref="ConnectsTo"/>), the post footprint, and
    /// the blockstate property values their multipart file expects (<see cref="Block.GetBlockState"/>).
    /// </summary>
    public abstract class BlockConnecting : Block
    {
        // Engine axes: north = -Z (Back), east = +X (Right), south = +Z (Front), west = -X (Left).
        protected static readonly BlockFace[] Sides = { BlockFace.Back, BlockFace.Right, BlockFace.Front, BlockFace.Left };
        protected static readonly string[] SideNames = { "north", "east", "south", "west" };

        protected BlockConnecting(string name) : base(name)
        {
        }

        protected override CreativeTab DefaultCreativeTab => CreativeTab.BuildingBlocks;

        public override bool IsFullBlock(WorldBase world, Vector3D<int> blockPos) => false;

        /// <summary>Block-local footprint of the central post (and the cross-section of each arm), e.g. 0.375..0.625
        /// for a fence, 0.25..0.75 for a wall.</summary>
        protected abstract float PostMin { get; }
        protected abstract float PostMax { get; }

        /// <summary>Collision height — 1.5 (taller than a block) so players can't jump a fence/wall, as in MC.</summary>
        protected virtual float CollisionHeight => 1.5f;

        /// <summary>Whether this block joins toward the given neighbour (a like block, or a solid full face).</summary>
        protected abstract bool ConnectsTo(WorldBase world, Vector3D<int> neighborPos, Block neighbor);

        protected bool Connects(WorldBase world, Vector3D<int> pos, BlockFace side)
        {
            var np = pos + side.GetNormali();
            return ConnectsTo(world, np, world.GetBlock(np));
        }

        public override void GetCollisionBoxes(WorldBase world, Vector3D<int> blockPos, List<AxisAlignedBoundingBox> boxes)
        {
            float lo = PostMin, hi = PostMax, h = CollisionHeight;
            boxes.Add(Box(lo, 0f, lo, hi, h, hi));                                                  // post
            if (Connects(world, blockPos, BlockFace.Back)) boxes.Add(Box(lo, 0f, 0f, hi, h, lo));   // north -Z
            if (Connects(world, blockPos, BlockFace.Front)) boxes.Add(Box(lo, 0f, hi, hi, h, 1f));   // south +Z
            if (Connects(world, blockPos, BlockFace.Right)) boxes.Add(Box(hi, 0f, lo, 1f, h, hi));   // east +X
            if (Connects(world, blockPos, BlockFace.Left)) boxes.Add(Box(0f, 0f, lo, lo, h, hi));    // west -X
        }

        private static AxisAlignedBoundingBox Box(float x0, float y0, float z0, float x1, float y1, float z1)
            => new AxisAlignedBoundingBox(new Vector3D<float>(x0, y0, z0), new Vector3D<float>(x1, y1, z1));
    }
}

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
    /// A half-height slab whose appearance comes from the pack's <c>blockstates/&lt;id&gt;_slab.json</c>
    /// (selected by <see cref="GetBlockState"/>): <c>type=bottom</c> → lower half, <c>type=top</c> → upper half,
    /// <c>type=double</c> → a full block. The half lives in block metadata (bottom 0 / top 1 / double 2), chosen
    /// at placement from the clicked face + cursor height, and <see cref="GetCollisionBoxes"/> matches it so the
    /// player walks up slabs. Double-slab merging (placing a slab into the matching opposite half) is not yet
    /// wired — see docs/known-issues.md.
    /// </summary>
    public class BlockSlab : Block
    {
        private const int Bottom = 0;
        private const int Top = 1;
        private const int Double = 2;
        private static readonly string[] TypeNames = { "bottom", "top", "double" };

        private readonly float _hardness;
        private readonly ToolType _tool;
        private readonly int _toolTier;
        private readonly bool _requiresTool;

        public BlockSlab(string name, string id, float hardness, ToolType tool, int toolTier = 0, bool requiresTool = false)
            : base(name)
        {
            MinecraftId = "minecraft:" + id + "_slab";
            ModelPath = "minecraft:block/" + id + "_slab";
            BlockStateId = "minecraft:" + id + "_slab";
            _hardness = hardness;
            _tool = tool;
            _toolTier = toolTier;
            _requiresTool = requiresTool;
        }

        public override float Hardness => _hardness;
        public override ToolType PreferredTool => _tool;
        public override int RequiredToolTier => _toolTier;
        public override bool RequiresCorrectTool => _requiresTool;

        public override bool IsFullBlock(WorldBase world, Vector3D<int> blockPos) => Half(world, blockPos) == Double;

        public override IReadOnlyDictionary<string, string> GetBlockState(WorldBase world, Vector3D<int> blockPos)
            => new Dictionary<string, string> { { "type", TypeNames[Half(world, blockPos)] } };

        public override int GetPlacementMetadata(EntityPlayer player, BlockRaytraceResult ray)
        {
            if (ray == null) return Bottom;
            if (ray.Face == BlockFace.Top) return Bottom;     // placed on a block's top -> bottom slab
            if (ray.Face == BlockFace.Bottom) return Top;      // placed under a block -> top slab
            // Clicked a side: the cursor's height within the cell picks the half (upper half -> top slab).
            var frac = ray.Point.Y - MathF.Floor(ray.Point.Y);
            return frac >= 0.5f ? Top : Bottom;
        }

        public override void OnPlaced(WorldBase world, Vector3D<int> blockPos, EntityPlayer player, int metadata)
            => world.SetBlockData(blockPos, new BlockDataMetadata(metadata));

        public override void GetCollisionBoxes(WorldBase world, Vector3D<int> blockPos, List<AxisAlignedBoundingBox> boxes)
        {
            var half = Half(world, blockPos);
            if (half == Double)
            {
                boxes.Add(DefaultAlignedBoundingBox);
                return;
            }
            var minY = half == Top ? 0.5f : 0f;
            var maxY = half == Top ? 1f : 0.5f;
            boxes.Add(new AxisAlignedBoundingBox(new Vector3D<float>(0f, minY, 0f), new Vector3D<float>(1f, maxY, 1f)));
        }

        private static int Half(WorldBase world, Vector3D<int> pos)
            => (world.GetBlockData(pos) as BlockDataMetadata)?.Metadata ?? Bottom;
    }
}

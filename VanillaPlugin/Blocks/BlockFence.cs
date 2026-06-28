using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Items;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// A wooden fence: connects to other fences and to solid blocks, rendered from the pack's multipart
    /// <c>&lt;wood&gt;_fence</c> blockstate (post + a side arm per connection). 1.5 blocks tall for collision so
    /// it can't be jumped. The held/inventory model is the pack's <c>_fence_inventory</c> segment.
    /// </summary>
    public class BlockFence : BlockConnecting
    {
        public BlockFence(string name, string wood) : base(name)
        {
            MinecraftId = "minecraft:" + wood + "_fence";
            ModelPath = "minecraft:block/" + wood + "_fence_inventory";
            BlockStateId = "minecraft:" + wood + "_fence";
        }

        public override float Hardness => 2.0f;
        public override ToolType PreferredTool => ToolType.Axe;

        protected override float PostMin => 0.375f;
        protected override float PostMax => 0.625f;

        protected override bool ConnectsTo(WorldBase world, Vector3D<int> neighborPos, Block neighbor)
            => neighbor is BlockFence || neighbor.IsOpaqueFullBlock(world, neighborPos);

        public override IReadOnlyDictionary<string, string> GetBlockState(WorldBase world, Vector3D<int> blockPos)
        {
            var state = new Dictionary<string, string>();
            for (var i = 0; i < Sides.Length; i++)
                state[SideNames[i]] = Connects(world, blockPos, Sides[i]) ? "true" : "false";
            return state;
        }
    }
}

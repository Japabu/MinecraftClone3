using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Items;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// A stone wall: connects to other walls and to solid blocks, rendered from the pack's multipart
    /// <c>&lt;stone&gt;_wall</c> blockstate. Connections are emitted as <c>low</c> and the centre post is always
    /// shown (the vanilla post-cull and the <c>tall</c> height-under-overhang variants are simplified away —
    /// see docs/known-issues.md). Mined with a pickaxe like its base block.
    /// </summary>
    public class BlockWall : BlockConnecting
    {
        public BlockWall(string name, string stone) : base(name)
        {
            MinecraftId = "minecraft:" + stone + "_wall";
            ModelPath = "minecraft:block/" + stone + "_wall_inventory";
            BlockStateId = "minecraft:" + stone + "_wall";
        }

        public override float Hardness => 2.0f;
        public override ToolType PreferredTool => ToolType.Pickaxe;
        public override bool RequiresCorrectTool => true;

        protected override float PostMin => 0.25f;
        protected override float PostMax => 0.75f;

        protected override bool ConnectsTo(WorldBase world, Vector3D<int> neighborPos, Block neighbor)
            => neighbor is BlockWall || neighbor.IsOpaqueFullBlock(world, neighborPos);

        public override IReadOnlyDictionary<string, string> GetBlockState(WorldBase world, Vector3D<int> blockPos)
        {
            var state = new Dictionary<string, string> { { "up", "true" } };
            for (var i = 0; i < Sides.Length; i++)
                state[SideNames[i]] = Connects(world, blockPos, Sides[i]) ? "low" : "none";
            return state;
        }
    }
}

using MinecraftClone3API.Blocks;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    public class BlockBasic : Block
    {
        private readonly bool _fullBlock;
        private readonly float _hardness;
        private readonly ToolType _tool;
        private readonly int _toolTier;
        private readonly bool _requiresTool;

        public BlockBasic(string name, string modelPath, bool fullBlock, float hardness = 1.5f,
            ToolType tool = ToolType.None, int toolTier = 0, bool requiresTool = false) : base(name)
        {
            _fullBlock = fullBlock;
            _hardness = hardness;
            _tool = tool;
            _toolTier = toolTier;
            _requiresTool = requiresTool;

            MinecraftId = Identifier.FromResourcePath(modelPath);
            ModelPath = modelPath;
        }

        public override bool IsFullBlock(WorldBase world, Vector3D<int> blockPos) => _fullBlock;
        public override float Hardness => _hardness;
        public override ToolType PreferredTool => _tool;
        public override int RequiredToolTier => _toolTier;
        public override bool RequiresCorrectTool => _requiresTool;
    }
}

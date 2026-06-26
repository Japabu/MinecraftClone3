using MinecraftClone3API.Items;
using MinecraftClone3API.Util;

namespace VanillaPlugin.Items
{
    /// <summary>A mining tool (pickaxe/axe/shovel). The material fixes its <see cref="MiningSpeed"/> multiplier
    /// and harvest <see cref="ToolTier"/>; mining is fast only against blocks whose
    /// <see cref="MinecraftClone3API.Blocks.Block.PreferredTool"/> matches <see cref="ToolType"/>.</summary>
    public class ItemTool : Item
    {
        private readonly string _texturePath;
        private readonly string _minecraftId;
        private readonly ToolType _toolType;
        private readonly float _miningSpeed;
        private readonly int _tier;

        public ItemTool(string name, string texturePath, ToolType toolType, float miningSpeed, int tier) : base(name)
        {
            _texturePath = texturePath;
            _minecraftId = Identifier.FromResourcePath(texturePath);
            _toolType = toolType;
            _miningSpeed = miningSpeed;
            _tier = tier;
        }

        public override string TexturePath => _texturePath;
        public override string MinecraftId => _minecraftId;
        public override int MaxStackSize => 1;
        public override ToolType ToolType => _toolType;
        public override float MiningSpeed => _miningSpeed;
        public override int ToolTier => _tier;
    }
}

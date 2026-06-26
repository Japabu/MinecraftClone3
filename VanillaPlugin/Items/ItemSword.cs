using MinecraftClone3API.Items;
using MinecraftClone3API.Util;

namespace VanillaPlugin.Items
{
    /// <summary>A melee weapon. The material fixes its <see cref="AttackDamage"/> (half-hearts dealt when
    /// left-clicking an entity); it also counts as a <see cref="ToolType.Sword"/> tool with the material's
    /// harvest tier. Like a tool, it doesn't stack.</summary>
    public class ItemSword : Item
    {
        private readonly string _texturePath;
        private readonly string _minecraftId;
        private readonly float _attackDamage;
        private readonly int _tier;

        public ItemSword(string name, string texturePath, float attackDamage, int tier) : base(name)
        {
            _texturePath = texturePath;
            _minecraftId = Identifier.FromResourcePath(texturePath);
            _attackDamage = attackDamage;
            _tier = tier;
        }

        public override string TexturePath => _texturePath;
        public override string MinecraftId => _minecraftId;
        public override int MaxStackSize => 1;
        public override float AttackDamage => _attackDamage;
        public override ToolType ToolType => ToolType.Sword;
        public override int ToolTier => _tier;
    }
}

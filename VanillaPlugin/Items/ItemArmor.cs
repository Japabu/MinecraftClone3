using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Items
{
    /// <summary>A wearable armor piece. Right-clicking equips it into its <see cref="ArmorSlot"/> (swapping
    /// out whatever was worn), and while worn its <see cref="ArmorDefense"/> points reduce incoming melee
    /// damage. Doesn't stack.</summary>
    public class ItemArmor : Item
    {
        private readonly string _texturePath;
        private readonly string _minecraftId;
        private readonly ArmorSlot _slot;
        private readonly int _defense;

        public ItemArmor(string name, string texturePath, ArmorSlot slot, int defense) : base(name)
        {
            _texturePath = texturePath;
            _minecraftId = Identifier.FromResourcePath(texturePath);
            _slot = slot;
            _defense = defense;
        }

        protected override CreativeTab DefaultCreativeTab => CreativeTab.Combat;

        public override string TexturePath => _texturePath;
        public override string MinecraftId => _minecraftId;
        public override int MaxStackSize => 1;
        public override ArmorSlot? ArmorSlot => _slot;
        public override int ArmorDefense => _defense;

        public override bool IsUsable => true;
        public override bool RefreshInventoryAfterUse => true;

        /// <summary>Equips the held piece: swap it into its armor slot, putting whatever was worn back in hand.</summary>
        public override void OnUseServer(WorldServer world, EntityPlayer player, Vector3D<float> position)
        {
            var inv = player.Inventory;
            if (inv == null) return;

            var hand = inv.SelectedHotbar;
            var idx = (int) _slot;
            var current = inv.Armor[idx];
            inv.Armor[idx] = inv.Slots[hand].WithCount(1);
            inv.Slots[hand] = current;
        }
    }
}

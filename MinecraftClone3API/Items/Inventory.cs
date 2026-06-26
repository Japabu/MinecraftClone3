using System.IO;

namespace MinecraftClone3API.Items
{
    /// <summary>
    /// A player's item storage: a 9-slot hotbar (indices 0..8) followed by a 27-slot main grid
    /// (indices 9..35). The server owns the authoritative copy per <c>ClientSession</c>; the client keeps
    /// a replica synced via inventory packets. <see cref="SelectedHotbar"/> is the active hotbar slot,
    /// which selects what the player places.
    /// </summary>
    public class Inventory
    {
        public const int HotbarSize = 9;
        public const int MainSize = 27;
        public const int Size = HotbarSize + MainSize;
        public const int ArmorSize = 4;

        /// <summary>Slot-index offset used by <c>InventoryActionPacket</c> to address an armor slot (index
        /// <see cref="ArmorActionBase"/> + armorIndex) instead of a main/hotbar slot.</summary>
        public const int ArmorActionBase = 100;

        public readonly ItemStack[] Slots = new ItemStack[Size];

        /// <summary>The four worn armor pieces, indexed by <see cref="ArmorSlot"/> (helmet/chest/legs/boots).
        /// Kept separate from <see cref="Slots"/> so existing slot indexing/serialization is unaffected;
        /// <see cref="Add"/> never auto-fills it.</summary>
        public readonly ItemStack[] Armor = new ItemStack[ArmorSize];

        public int SelectedHotbar;

        public ItemStack SelectedItem => Slots[SelectedHotbar];

        /// <summary>Inserts a stack, merging into matching partial stacks first, then filling empty slots.
        /// Returns true if the whole stack fit; otherwise <paramref name="stack"/> holds the remainder.</summary>
        public bool Add(ref ItemStack stack)
        {
            var max = stack.Item?.MaxStackSize ?? ItemStack.MaxStackSize;
            for (var i = 0; i < Size && !stack.IsEmpty; i++)
            {
                if (Slots[i].IsEmpty || !Slots[i].SameItem(stack)) continue;
                var room = max - Slots[i].Count;
                if (room <= 0) continue;
                var moved = room < stack.Count ? room : stack.Count;
                Slots[i] = Slots[i].WithCount(Slots[i].Count + moved);
                stack = stack.WithCount(stack.Count - moved);
            }

            for (var i = 0; i < Size && !stack.IsEmpty; i++)
            {
                if (!Slots[i].IsEmpty) continue;
                var moved = stack.Count < max ? stack.Count : max;
                Slots[i] = stack.WithCount(moved);
                stack = stack.WithCount(stack.Count - moved);
            }

            return stack.IsEmpty;
        }

        /// <summary>Total defense points from worn armor, used by survival damage reduction.</summary>
        public int ArmorDefense()
        {
            var total = 0;
            for (var i = 0; i < ArmorSize; i++)
            {
                var item = Armor[i].Item;
                if (item != null) total += item.ArmorDefense;
            }
            return total;
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(SelectedHotbar);
            for (var i = 0; i < Size; i++)
                Slots[i].Write(writer);
            for (var i = 0; i < ArmorSize; i++)
                Armor[i].Write(writer);
        }

        public void Read(BinaryReader reader)
        {
            SelectedHotbar = reader.ReadInt32();
            for (var i = 0; i < Size; i++)
                Slots[i] = ItemStack.Read(reader);
            for (var i = 0; i < ArmorSize; i++)
                Armor[i] = ItemStack.Read(reader);
        }
    }
}

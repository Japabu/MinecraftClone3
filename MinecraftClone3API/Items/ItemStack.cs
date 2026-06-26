using System.IO;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Items
{
    /// <summary>
    /// A quantity of one item type in an inventory slot. An item is referenced by its registry
    /// <see cref="ItemId"/> (a block's auto-generated <see cref="ItemBlock"/> or a standalone item) plus
    /// placement <see cref="Metadata"/>; <see cref="Empty"/> (item id 0 / zero count) marks a vacant slot.
    /// Value type — copy-by-assignment, never aliased between slots.
    /// </summary>
    public struct ItemStack
    {
        public const int MaxStackSize = 64;

        public ushort ItemId;
        public int Count;
        public int Metadata;

        public ItemStack(ushort itemId, int count, int metadata = 0)
        {
            ItemId = itemId;
            Count = count;
            Metadata = metadata;
        }

        public static readonly ItemStack Empty = new ItemStack(0, 0, 0);

        public bool IsEmpty => ItemId == 0 || Count <= 0;

        /// <summary>The registered item this stack holds, or null when empty.</summary>
        public Item Item => GameRegistry.GetItem(ItemId);

        public ItemStack WithCount(int count) => new ItemStack(ItemId, count, Metadata);

        public bool SameItem(ItemStack other) => ItemId == other.ItemId && Metadata == other.Metadata;

        public void Write(BinaryWriter writer)
        {
            // Reference the item by its stable registry name, not its session-local id, so a saved inventory
            // survives items being added/removed/reordered. Empty stacks write the empty name.
            var item = GameRegistry.GetItem(ItemId);
            writer.Write(item == null ? "" : item.RegistryKey);
            writer.Write(Count);
            writer.Write(Metadata);
        }

        public static ItemStack Read(BinaryReader reader)
        {
            var key = reader.ReadString();
            var count = reader.ReadInt32();
            var metadata = reader.ReadInt32();
            if (string.IsNullOrEmpty(key)) return new ItemStack(0, count, metadata);
            return new ItemStack(GameRegistry.ItemRegistry.GetOrRegisterUnknown(key), count, metadata);
        }
    }
}

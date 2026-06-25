using System.IO;

namespace MinecraftClone3API.Items
{
    /// <summary>
    /// A quantity of one item type in an inventory slot. An item is currently a block reference
    /// (<see cref="BlockId"/> + placement <see cref="Metadata"/>); <see cref="Empty"/> (block id 0 / zero
    /// count) marks a vacant slot. Value type — copy-by-assignment, never aliased between slots.
    /// </summary>
    public struct ItemStack
    {
        public const int MaxStackSize = 64;

        public ushort BlockId;
        public int Count;
        public int Metadata;

        public ItemStack(ushort blockId, int count, int metadata = 0)
        {
            BlockId = blockId;
            Count = count;
            Metadata = metadata;
        }

        public static readonly ItemStack Empty = new ItemStack(0, 0, 0);

        public bool IsEmpty => BlockId == 0 || Count <= 0;

        public ItemStack WithCount(int count) => new ItemStack(BlockId, count, Metadata);

        public bool SameItem(ItemStack other) => BlockId == other.BlockId && Metadata == other.Metadata;

        public void Write(BinaryWriter writer)
        {
            writer.Write(BlockId);
            writer.Write(Count);
            writer.Write(Metadata);
        }

        public static ItemStack Read(BinaryReader reader)
            => new ItemStack(reader.ReadUInt16(), reader.ReadInt32(), reader.ReadInt32());
    }
}

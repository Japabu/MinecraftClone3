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

        public readonly ItemStack[] Slots = new ItemStack[Size];

        public int SelectedHotbar;

        public ItemStack SelectedItem => Slots[SelectedHotbar];

        public void Write(BinaryWriter writer)
        {
            writer.Write(SelectedHotbar);
            for (var i = 0; i < Size; i++)
                Slots[i].Write(writer);
        }

        public void Read(BinaryReader reader)
        {
            SelectedHotbar = reader.ReadInt32();
            for (var i = 0; i < Size; i++)
                Slots[i] = ItemStack.Read(reader);
        }
    }
}

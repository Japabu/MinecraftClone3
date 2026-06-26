using System.IO;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Items;

namespace VanillaPlugin.BlockDatas
{
    /// <summary>
    /// The persistent, server-authoritative state of a chest: its facing and 27 storage slots. Implements
    /// <see cref="ContainerBlockData"/> so the engine streams it to a viewing client and applies that client's
    /// slot edits generically (a chest has no progress fields, so <see cref="SyncFields"/> is empty). The screen
    /// (<c>GuiChest</c>) reads the slots; <see cref="BlockChest"/> drops them when the block is broken.
    /// </summary>
    public class BlockDataChest : ContainerBlockData
    {
        public const int SlotCount = 27;

        public byte Facing;

        private readonly ItemStack[] _slots;

        public BlockDataChest()
        {
            _slots = new ItemStack[SlotCount];
            for (var i = 0; i < SlotCount; i++) _slots[i] = ItemStack.Empty;
        }

        public BlockDataChest(byte facing) : this()
        {
            Facing = facing;
        }

        public override ItemStack[] Slots => _slots;

        public override void SetSlot(int index, ItemStack stack)
        {
            if (index >= 0 && index < _slots.Length) _slots[index] = stack;
        }

        public override int[] SyncFields => System.Array.Empty<int>();

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(Facing);
            foreach (var slot in _slots) slot.Write(writer);
        }

        public override void Deserialize(BinaryReader reader)
        {
            Facing = reader.ReadByte();
            for (var i = 0; i < _slots.Length; i++) _slots[i] = ItemStack.Read(reader);
        }
    }
}

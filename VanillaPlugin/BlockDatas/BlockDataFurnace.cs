using System.IO;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Items;

namespace VanillaPlugin.BlockDatas
{
    /// <summary>
    /// The persistent, server-authoritative state of a furnace: its facing, the three item slots (input, fuel,
    /// output) and the smelting progress counters. Implements <see cref="ContainerBlockData"/> so the engine can
    /// stream it to a viewing client and apply that client's slot edits generically. <see cref="BlockFurnace"/>
    /// runs the smelting on the server tick; the screen reads the slots and the <see cref="SyncFields"/>.
    /// </summary>
    public class BlockDataFurnace : ContainerBlockData
    {
        public const int SlotInput = 0;
        public const int SlotFuel = 1;
        public const int SlotOutput = 2;

        public byte Facing;

        private readonly ItemStack[] _slots = { ItemStack.Empty, ItemStack.Empty, ItemStack.Empty };

        /// <summary>Ticks of fuel left in the current burn, and the burn's full duration (drives the flame).</summary>
        public int BurnTime;
        public int BurnTimeTotal;

        /// <summary>Ticks the current item has been cooking, and the recipe's total cook time (drives the arrow).</summary>
        public int CookTime;
        public int CookTimeTotal;

        public BlockDataFurnace()
        {
        }

        public BlockDataFurnace(byte facing)
        {
            Facing = facing;
        }

        public bool IsBurning => BurnTime > 0;

        public override ItemStack[] Slots => _slots;

        public override void SetSlot(int index, ItemStack stack)
        {
            if (index >= 0 && index < _slots.Length) _slots[index] = stack;
        }

        public override int[] SyncFields => new[] { BurnTime, BurnTimeTotal, CookTime, CookTimeTotal };

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(Facing);
            foreach (var slot in _slots) slot.Write(writer);
            writer.Write(BurnTime);
            writer.Write(BurnTimeTotal);
            writer.Write(CookTime);
            writer.Write(CookTimeTotal);
        }

        public override void Deserialize(BinaryReader reader)
        {
            Facing = reader.ReadByte();
            for (var i = 0; i < _slots.Length; i++) _slots[i] = ItemStack.Read(reader);
            BurnTime = reader.ReadInt32();
            BurnTimeTotal = reader.ReadInt32();
            CookTime = reader.ReadInt32();
            CookTimeTotal = reader.ReadInt32();
        }
    }
}

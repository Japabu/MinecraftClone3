using MinecraftClone3API.Items;

namespace MinecraftClone3API.Client.Blocks
{
    /// <summary>
    /// The client's local replica of an open container block's server state (e.g. a furnace): its item
    /// <see cref="Slots"/> and integer progress <see cref="Fields"/>. Updated from each
    /// <c>ContainerStatePacket</c> and read by the container's screen; the screen edits a slot optimistically
    /// here and mirrors the edit up with a <c>ContainerSlotPacket</c>, exactly as inventory slots do.
    /// </summary>
    public class ContainerView
    {
        public readonly ItemStack[] Slots;
        public readonly int[] Fields;

        public ContainerView(int slotCount, int fieldCount)
        {
            Slots = new ItemStack[slotCount];
            for (var i = 0; i < slotCount; i++) Slots[i] = ItemStack.Empty;
            Fields = new int[fieldCount];
        }
    }
}

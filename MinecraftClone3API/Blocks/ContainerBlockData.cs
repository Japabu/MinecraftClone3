using MinecraftClone3API.Items;

namespace MinecraftClone3API.Blocks
{
    /// <summary>
    /// A <see cref="BlockData"/> that backs a server-synced container block (e.g. a furnace): a fixed set of
    /// item <see cref="Slots"/> plus a few integer <see cref="SyncFields"/> (progress counters). The engine's
    /// networking treats it generically — it streams the slots and fields to clients that have the block open
    /// and applies their slot edits — so the concrete container type (in a content plugin) need not be known
    /// to the API. The block's GUI interprets the slot/field layout.
    /// </summary>
    public abstract class ContainerBlockData : BlockData
    {
        /// <summary>The container's item slots, in a stable order. The live backing array.</summary>
        public abstract ItemStack[] Slots { get; }

        /// <summary>Replace one slot's stack (a client edit; trusted, as inventory edits are).</summary>
        public abstract void SetSlot(int index, ItemStack stack);

        /// <summary>The integer progress fields shown by the GUI (e.g. burn/cook counters). Order is the
        /// container's own contract with its screen.</summary>
        public abstract int[] SyncFields { get; }
    }
}

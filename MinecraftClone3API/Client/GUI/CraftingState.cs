using System;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// Client-side crafting logic shared by the 3×3 crafting-table screen and the 2×2 grid in the creative
    /// inventory: an N×N scratch grid plus the cursor-held stack, with the standard pick/place/swap slot
    /// interaction, recipe matching for the result, and consuming ingredients on take. The N×N grid is local
    /// scratch (not part of the inventory); player-inventory slots are mutated on the server-authoritative
    /// replica and mirrored up with <see cref="WorldClient.SendInventoryAction"/>.
    /// </summary>
    public class CraftingState
    {
        public readonly int Size;
        public readonly ItemStack[] Grid;

        private readonly WorldClient _world;

        public CraftingState(WorldClient world, int size)
        {
            _world = world;
            Size = size;
            Grid = new ItemStack[size * size];
            for (var i = 0; i < Grid.Length; i++) Grid[i] = ItemStack.Empty;
        }

        /// <summary>The crafting result for the current grid (empty if nothing matches).</summary>
        public ItemStack Result => GameRegistry.MatchRecipe(Grid, Size, Size);

        /// <summary>Standard slot click against the cursor: pick up, drop, merge same items, or swap.</summary>
        public void InteractGrid(int index, ref ItemStack cursor) => Interact(ref Grid[index], ref cursor);

        public void InteractInventory(int index, ref ItemStack cursor)
        {
            Interact(ref _world.Inventory.Slots[index], ref cursor);
            _world.SendInventoryAction(index, _world.Inventory.Slots[index]);
        }

        private static void Interact(ref ItemStack slot, ref ItemStack cursor)
        {
            if (cursor.IsEmpty)
            {
                cursor = slot;
                slot = ItemStack.Empty;
                return;
            }

            if (slot.IsEmpty)
            {
                slot = cursor;
                cursor = ItemStack.Empty;
                return;
            }

            if (slot.SameItem(cursor))
            {
                var max = slot.Item?.MaxStackSize ?? ItemStack.MaxStackSize;
                var move = Math.Min(max - slot.Count, cursor.Count);
                slot.Count += move;
                cursor.Count -= move;
                if (cursor.Count <= 0) cursor = ItemStack.Empty;
                return;
            }

            var tmp = slot;
            slot = cursor;
            cursor = tmp;
        }

        /// <summary>Take one crafted batch into the cursor, consuming one item from each filled grid cell.
        /// No-op if nothing matches or the cursor can't hold the result.</summary>
        public void TakeResult(ref ItemStack cursor)
        {
            var result = Result;
            if (result.IsEmpty) return;

            var max = result.Item?.MaxStackSize ?? ItemStack.MaxStackSize;
            if (!cursor.IsEmpty && (!cursor.SameItem(result) || cursor.Count + result.Count > max)) return;

            for (var i = 0; i < Grid.Length; i++)
            {
                if (Grid[i].IsEmpty) continue;
                Grid[i].Count--;
                if (Grid[i].Count <= 0) Grid[i] = ItemStack.Empty;
            }

            if (cursor.IsEmpty) cursor = result;
            else cursor.Count += result.Count;
        }

        /// <summary>Returns any items left in the grid (and the cursor) to the player inventory — call when the
        /// screen closes so nothing is lost. Items that don't fit are dropped (lost).</summary>
        public void ReturnAllToInventory(ref ItemStack cursor)
        {
            for (var i = 0; i < Grid.Length; i++)
            {
                if (!Grid[i].IsEmpty) AddToInventory(Grid[i]);
                Grid[i] = ItemStack.Empty;
            }

            if (!cursor.IsEmpty) AddToInventory(cursor);
            cursor = ItemStack.Empty;
        }

        private void AddToInventory(ItemStack stack)
        {
            var slots = _world.Inventory.Slots;
            var max = stack.Item?.MaxStackSize ?? ItemStack.MaxStackSize;

            for (var i = 0; i < slots.Length && stack.Count > 0; i++)
            {
                if (slots[i].IsEmpty || !slots[i].SameItem(stack)) continue;
                var move = Math.Min(max - slots[i].Count, stack.Count);
                if (move <= 0) continue;
                slots[i].Count += move;
                stack.Count -= move;
                _world.SendInventoryAction(i, slots[i]);
            }

            for (var i = 0; i < slots.Length && stack.Count > 0; i++)
            {
                if (!slots[i].IsEmpty) continue;
                slots[i] = stack;
                stack.Count = 0;
                _world.SendInventoryAction(i, slots[i]);
            }
        }
    }
}

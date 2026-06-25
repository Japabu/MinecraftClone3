using System;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// The backing state of a crafting grid: an N×N scratch grid (local, not part of the inventory) plus recipe
    /// matching for the result and the rules for consuming ingredients and returning leftovers. The cursor and
    /// all slot interaction live in <see cref="ContainerScreen"/>; this just owns the grid contents. Player
    /// inventory edits are mirrored to the server-authoritative replica via
    /// <see cref="WorldClient.SendInventoryAction"/>.
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

        /// <summary>Consume one item from each filled grid cell — one crafted batch's ingredients.</summary>
        public void ConsumeOne()
        {
            for (var i = 0; i < Grid.Length; i++)
            {
                if (Grid[i].IsEmpty) continue;
                Grid[i] = Grid[i].Count - 1 <= 0 ? ItemStack.Empty : Grid[i].WithCount(Grid[i].Count - 1);
            }
        }

        /// <summary>Return any items left in the grid to the player inventory — call when the screen closes so
        /// nothing is lost. Items that don't fit are dropped (lost).</summary>
        public void ReturnGridToInventory()
        {
            for (var i = 0; i < Grid.Length; i++)
            {
                if (!Grid[i].IsEmpty) AddToInventory(Grid[i]);
                Grid[i] = ItemStack.Empty;
            }
        }

        /// <summary>Merge a stack into the player inventory (filling partial stacks first, then empty slots),
        /// mirroring each touched slot to the server.</summary>
        public void AddToInventory(ItemStack stack)
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

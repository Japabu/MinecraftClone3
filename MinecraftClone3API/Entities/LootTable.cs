using System;
using System.Collections.Generic;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Entities
{
    /// <summary>One entry in a <see cref="LootTable"/>: drops between <see cref="Min"/> and <see cref="Max"/>
    /// of an item (resolved lazily by registry key, since loot is declared before all items are registered),
    /// gated by <see cref="Chance"/>.</summary>
    public sealed class LootDrop
    {
        public readonly string ItemKey;
        public readonly int Min;
        public readonly int Max;
        public readonly float Chance;

        public LootDrop(string itemKey, int min, int max, float chance = 1f)
        {
            ItemKey = itemKey;
            Min = min;
            Max = max;
            Chance = chance;
        }
    }

    /// <summary>What a creature drops when it dies. GL-free; rolled server-side by <see cref="EntityCombat"/>,
    /// which spawns the resulting stacks as dropped items.</summary>
    public sealed class LootTable
    {
        private readonly LootDrop[] _drops;

        public LootTable(params LootDrop[] drops)
        {
            _drops = drops;
        }

        /// <summary>Rolls each drop independently and yields the resulting non-empty stacks.</summary>
        public IEnumerable<ItemStack> Roll(Random rng)
        {
            foreach (var drop in _drops)
            {
                if (drop.Chance < 1f && rng.NextDouble() > drop.Chance) continue;
                var count = drop.Min >= drop.Max ? drop.Max : drop.Min + rng.Next(drop.Max - drop.Min + 1);
                if (count <= 0) continue;
                if (GameRegistry.TryGetItem(drop.ItemKey, out var item))
                    yield return new ItemStack(item.Id, count);
            }
        }
    }
}

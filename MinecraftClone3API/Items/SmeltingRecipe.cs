using MinecraftClone3API.Util;

namespace MinecraftClone3API.Items
{
    /// <summary>
    /// A furnace smelting recipe: a single input item (a <em>set</em> of acceptable ids, so a Minecraft tag
    /// like <c>#logs</c> or an explicit list of alternatives all map to the items we actually have) that
    /// produces a <see cref="Result"/> after <see cref="CookingTime"/> server ticks. Loaded from the resource
    /// pack's <c>minecraft:smelting</c> recipes; matching is a cheap id-set membership test, metadata ignored.
    /// </summary>
    public sealed class SmeltingRecipe : RegistryEntry
    {
        public ushort[] Ingredient;
        public ItemStack Result;
        public int CookingTime;

        public SmeltingRecipe(string name) : base(name)
        {
        }

        public bool Matches(ItemStack input)
        {
            if (input.IsEmpty || Ingredient == null) return false;
            for (var i = 0; i < Ingredient.Length; i++)
                if (Ingredient[i] == input.ItemId) return true;
            return false;
        }
    }
}

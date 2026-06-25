using System.Collections.Generic;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Items
{
    /// <summary>Factory helpers that build <see cref="CraftingRecipe"/>s from registry keys (resolved to item
    /// ids at build time). Pass the result of these to <c>PluginContext.Register</c>.</summary>
    public static class Recipes
    {
        private static ushort Id(string key) => GameRegistry.GetItem(key).Id;

        /// <summary>A shaped recipe. <paramref name="pattern"/> is up to 3 rows of up to 3 chars; each char is
        /// mapped to an item key via <paramref name="keys"/> (space = empty). The pattern is trimmed of
        /// surrounding blanks and may be placed anywhere in the grid (and mirrored).</summary>
        public static ShapedRecipe Shaped(string name, string resultKey, int resultCount, string[] pattern,
            params (char Symbol, string Key)[] keys)
        {
            var map = new Dictionary<char, ushort> { [' '] = 0 };
            foreach (var k in keys) map[k.Symbol] = Id(k.Key);

            var height = pattern.Length;
            var width = 0;
            foreach (var row in pattern) if (row.Length > width) width = row.Length;

            var cells = new ushort[width * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var c = x < pattern[y].Length ? pattern[y][x] : ' ';
                    cells[y * width + x] = map[c];
                }

            return new ShapedRecipe(name)
            {
                Width = width,
                Height = height,
                Pattern = cells,
                Result = new ItemStack(Id(resultKey), resultCount)
            };
        }

        /// <summary>A shapeless recipe from a list of ingredient keys (one item per occupied grid cell).</summary>
        public static ShapelessRecipe Shapeless(string name, string resultKey, int resultCount,
            params string[] ingredientKeys)
        {
            var ingredients = new ushort[ingredientKeys.Length];
            for (var i = 0; i < ingredientKeys.Length; i++) ingredients[i] = Id(ingredientKeys[i]);

            return new ShapelessRecipe(name)
            {
                Ingredients = ingredients,
                Result = new ItemStack(Id(resultKey), resultCount)
            };
        }
    }
}

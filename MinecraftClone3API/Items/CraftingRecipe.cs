using System.Collections.Generic;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Items
{
    /// <summary>
    /// A crafting recipe: matches the contents of an N×N crafting grid and yields a result stack. Two kinds
    /// are provided — <see cref="ShapedRecipe"/> (a fixed pattern, placeable anywhere in the grid and
    /// mirrorable) and <see cref="ShapelessRecipe"/> (a set of ingredients in any arrangement). Ingredients
    /// and the result are item ids resolved from registry keys at registration time, so matching is a cheap
    /// id comparison. Metadata is ignored when matching.
    /// </summary>
    public abstract class CraftingRecipe : RegistryEntry
    {
        protected CraftingRecipe(string name) : base(name)
        {
        }

        public ItemStack Result;

        public abstract bool Matches(ItemStack[] grid, int width, int height);

        /// <summary>The non-empty cell ids of the grid, with the grid's bounding box, used by the matchers.</summary>
        protected static (int minX, int minY, int maxX, int maxY) BoundingBox(ItemStack[] grid, int width, int height)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if (!grid[y * width + x].IsEmpty)
                    {
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
            return (minX, minY, maxX, maxY);
        }
    }

    /// <summary>A shaped recipe: a <see cref="Width"/>×<see cref="Height"/> pattern of item ids (0 = empty)
    /// that must appear, ignoring surrounding empty rows/columns. Matches the pattern or its horizontal mirror.</summary>
    public sealed class ShapedRecipe : CraftingRecipe
    {
        public int Width;
        public int Height;
        public ushort[] Pattern; // row-major, length Width*Height, 0 = empty

        public ShapedRecipe(string name) : base(name)
        {
        }

        public override bool Matches(ItemStack[] grid, int width, int height)
        {
            var (minX, minY, maxX, maxY) = BoundingBox(grid, width, height);
            if (maxX < 0) return false; // empty grid
            if (maxX - minX + 1 != Width || maxY - minY + 1 != Height) return false;
            return MatchesAt(grid, width, minX, minY, false) || MatchesAt(grid, width, minX, minY, true);
        }

        private bool MatchesAt(ItemStack[] grid, int gridWidth, int offX, int offY, bool mirror)
        {
            for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    var px = mirror ? Width - 1 - x : x;
                    var want = Pattern[y * Width + px];
                    var have = grid[(offY + y) * gridWidth + (offX + x)].ItemId;
                    if (grid[(offY + y) * gridWidth + (offX + x)].IsEmpty) have = 0;
                    if (want != have) return false;
                }
            return true;
        }
    }

    /// <summary>A shapeless recipe: a multiset of ingredient item ids that must exactly fill the grid (one
    /// item per occupied cell), in any arrangement.</summary>
    public sealed class ShapelessRecipe : CraftingRecipe
    {
        public ushort[] Ingredients;

        public ShapelessRecipe(string name) : base(name)
        {
        }

        public override bool Matches(ItemStack[] grid, int width, int height)
        {
            var remaining = new List<ushort>(Ingredients);
            for (var i = 0; i < grid.Length; i++)
            {
                if (grid[i].IsEmpty) continue;
                if (!remaining.Remove(grid[i].ItemId)) return false;
            }
            return remaining.Count == 0;
        }
    }
}

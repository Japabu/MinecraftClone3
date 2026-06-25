using System.Collections.Generic;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Items
{
    /// <summary>
    /// A crafting recipe: matches the contents of an N×N crafting grid and yields a result stack. Two kinds
    /// are provided — <see cref="ShapedRecipe"/> (a fixed pattern, placeable anywhere in the grid and
    /// mirrorable) and <see cref="ShapelessRecipe"/> (a set of ingredients in any arrangement). Each ingredient
    /// cell is a <em>set</em> of acceptable item ids (so a Minecraft tag like <c>#planks</c> or an explicit
    /// list of alternatives all map to the items we actually have), and matching is a cheap id-set membership
    /// test. Metadata is ignored when matching.
    /// </summary>
    public abstract class CraftingRecipe : RegistryEntry
    {
        protected CraftingRecipe(string name) : base(name)
        {
        }

        public ItemStack Result;

        public abstract bool Matches(ItemStack[] grid, int width, int height);

        protected static bool SetContains(ushort[] set, ushort id)
        {
            if (set == null) return false;
            for (var i = 0; i < set.Length; i++)
                if (set[i] == id) return true;
            return false;
        }

        /// <summary>The bounding box of the non-empty grid cells, used by the matchers.</summary>
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

    /// <summary>A shaped recipe: a <see cref="Width"/>×<see cref="Height"/> grid of ingredient cells (each a set
    /// of acceptable ids; null/empty = empty cell) that must appear, ignoring surrounding empty rows/columns.
    /// Matches the pattern or its horizontal mirror.</summary>
    public sealed class ShapedRecipe : CraftingRecipe
    {
        public int Width;
        public int Height;
        public ushort[][] Pattern; // row-major, length Width*Height; each cell a set of ids, null/empty = empty

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
                    var cell = grid[(offY + y) * gridWidth + (offX + x)];
                    var wantEmpty = want == null || want.Length == 0;

                    if (cell.IsEmpty)
                    {
                        if (!wantEmpty) return false;
                    }
                    else
                    {
                        if (wantEmpty || !SetContains(want, cell.ItemId)) return false;
                    }
                }
            return true;
        }
    }

    /// <summary>A shapeless recipe: a multiset of ingredient cells (each a set of acceptable ids) that must
    /// exactly fill the grid (one item per occupied cell), in any arrangement.</summary>
    public sealed class ShapelessRecipe : CraftingRecipe
    {
        public ushort[][] Ingredients;

        public ShapelessRecipe(string name) : base(name)
        {
        }

        public override bool Matches(ItemStack[] grid, int width, int height)
        {
            var remaining = new List<ushort[]>(Ingredients);
            for (var i = 0; i < grid.Length; i++)
            {
                if (grid[i].IsEmpty) continue;

                var matched = -1;
                for (var j = 0; j < remaining.Count; j++)
                    if (SetContains(remaining[j], grid[i].ItemId))
                    {
                        matched = j;
                        break;
                    }

                if (matched < 0) return false;
                remaining.RemoveAt(matched);
            }
            return remaining.Count == 0;
        }
    }
}

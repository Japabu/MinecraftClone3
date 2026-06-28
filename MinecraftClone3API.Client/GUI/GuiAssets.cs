using System.Collections.Generic;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// Lazily loads and caches the official Minecraft GUI textures (from a resource pack the user supplies)
    /// by their in-jar asset path. Returns null when the resource pack is absent so callers can fall back to
    /// placeholder drawing; no Minecraft asset is ever shipped with the engine.
    /// </summary>
    public static class GuiAssets
    {
        // Modern (1.20.2+) HUD sprite layout: each HUD element is its own sprite PNG under sprites/hud/,
        // drawn whole (no sub-rect).
        public const string Hotbar = "minecraft/textures/gui/sprites/hud/hotbar.png";
        public const string HotbarSelection = "minecraft/textures/gui/sprites/hud/hotbar_selection.png";
        public const string Crosshair = "minecraft/textures/gui/sprites/hud/crosshair.png";
        public const string HeartContainer = "minecraft/textures/gui/sprites/hud/heart/container.png";
        public const string HeartFull = "minecraft/textures/gui/sprites/hud/heart/full.png";
        public const string HeartHalf = "minecraft/textures/gui/sprites/hud/heart/half.png";
        public const string FoodEmpty = "minecraft/textures/gui/sprites/hud/food_empty.png";
        public const string FoodFull = "minecraft/textures/gui/sprites/hud/food_full.png";
        public const string FoodHalf = "minecraft/textures/gui/sprites/hud/food_half.png";
        // The three creative-inventory panel backgrounds (195x136 used region of a 256x256 sheet): the plain
        // item grid for category tabs, the search variant (search box baked in), and the inventory variant
        // (armor slots + player-model frame baked in).
        public const string CreativeItemsTab = "minecraft/textures/gui/container/creative_inventory/tab_items.png";
        public const string CreativeSearchTab = "minecraft/textures/gui/container/creative_inventory/tab_item_search.png";
        public const string CreativeInventoryTab = "minecraft/textures/gui/container/creative_inventory/tab_inventory.png";

        // The 12x15 scrollbar knob is its own sprite (modern packs have no monolithic widgets.png), with a
        // dimmed variant drawn when there aren't enough items to scroll.
        public const string Scroller = "minecraft/textures/gui/sprites/container/creative_inventory/scroller.png";
        public const string ScrollerDisabled = "minecraft/textures/gui/sprites/container/creative_inventory/scroller_disabled.png";
        public const string Furnace = "minecraft/textures/gui/container/furnace.png";
        public const string FurnaceLit = "minecraft/textures/gui/sprites/container/furnace/lit_progress.png";
        public const string FurnaceBurn = "minecraft/textures/gui/sprites/container/furnace/burn_progress.png";
        public const string CraftingTable = "minecraft/textures/gui/container/crafting_table.png";
        public const string Inventory = "minecraft/textures/gui/container/inventory.png";
        public const string Generic54 = "minecraft/textures/gui/container/generic_54.png";

        private static readonly Dictionary<string, Texture> Cache = new Dictionary<string, Texture>();

        /// <summary>The 26x32 creative-tab button sprite for a tab in the given row/column and selected state.
        /// Modern packs ship one sprite per tab position (1-based <paramref name="column"/> 1..7), the left/
        /// middle/right variants differing only in which edge connects to the panel.</summary>
        public static string CreativeTabSprite(bool top, bool selected, int column)
        {
            var col = column < 1 ? 1 : column > 7 ? 7 : column;
            var row = top ? "top" : "bottom";
            var state = selected ? "selected" : "unselected";
            return $"minecraft/textures/gui/sprites/container/creative_inventory/tab_{row}_{state}_{col}.png";
        }

        /// <summary>The GUI texture at <paramref name="path"/>, or null if no resource pack provides it.</summary>
        public static Texture Get(string path)
        {
            if (Cache.TryGetValue(path, out var tex)) return tex;
            tex = ResourceReader.Exists(path) ? GlResources.ReadTexture(path) : null;
            Cache[path] = tex;
            return tex;
        }
    }
}

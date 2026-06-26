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
        public const string Widgets = "minecraft/textures/gui/widgets.png";

        // Modern (1.20.2+) HUD sprite layout: the old monolithic widgets.png / icons.png were split into
        // individual sprite PNGs. Each is drawn whole (no sub-rect).
        public const string Hotbar = "minecraft/textures/gui/sprites/hud/hotbar.png";
        public const string HotbarSelection = "minecraft/textures/gui/sprites/hud/hotbar_selection.png";
        public const string HeartContainer = "minecraft/textures/gui/sprites/hud/heart/container.png";
        public const string HeartFull = "minecraft/textures/gui/sprites/hud/heart/full.png";
        public const string HeartHalf = "minecraft/textures/gui/sprites/hud/heart/half.png";
        public const string FoodEmpty = "minecraft/textures/gui/sprites/hud/food_empty.png";
        public const string FoodFull = "minecraft/textures/gui/sprites/hud/food_full.png";
        public const string FoodHalf = "minecraft/textures/gui/sprites/hud/food_half.png";
        public const string CreativeTab = "minecraft/textures/gui/container/creative_inventory/tab_items.png";
        public const string Furnace = "minecraft/textures/gui/container/furnace.png";
        public const string FurnaceLit = "minecraft/textures/gui/sprites/container/furnace/lit_progress.png";
        public const string FurnaceBurn = "minecraft/textures/gui/sprites/container/furnace/burn_progress.png";
        public const string CraftingTable = "minecraft/textures/gui/container/crafting_table.png";
        public const string Inventory = "minecraft/textures/gui/container/inventory.png";

        private static readonly Dictionary<string, Texture> Cache = new Dictionary<string, Texture>();

        /// <summary>The GUI texture at <paramref name="path"/>, or null if no resource pack provides it.</summary>
        public static Texture Get(string path)
        {
            if (Cache.TryGetValue(path, out var tex)) return tex;
            tex = ResourceReader.Exists(path) ? ResourceReader.ReadTexture(path) : null;
            Cache[path] = tex;
            return tex;
        }
    }
}

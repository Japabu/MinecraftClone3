using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.WorldGen;

namespace MinecraftClone3API.Util
{
    public static class GameRegistry
    {
        internal static readonly BlockRegistry BlockRegistry = new BlockRegistry();
        internal static readonly ItemRegistry ItemRegistry = new ItemRegistry();
        internal static readonly Registry<CraftingRecipe> RecipeRegistry = new Registry<CraftingRecipe>();
        internal static readonly Registry<SmeltingRecipe> SmeltingRegistry = new Registry<SmeltingRecipe>();
        internal static readonly Registry<BlockDataRegistryEntry> BlockDataRegistry = new Registry<BlockDataRegistryEntry>();
        internal static readonly Registry<EntityDataRegistryEntry> EntityDataRegistry = new Registry<EntityDataRegistryEntry>();
        internal static readonly Registry<Biome> BiomeRegistry = new Registry<Biome>();
        internal static readonly Registry<Feature> FeatureRegistry = new Registry<Feature>();
        internal static readonly Registry<Dimension> DimensionRegistry = new Registry<Dimension>();
        internal static readonly EntityRegistry EntityRegistry = new EntityRegistry();

        /// <summary>The content-provided portal/dimension-travel rules (see <see cref="WorldGen.IDimensionPortals"/>),
        /// or null if no plugin registered any (then standing in a block never transfers dimensions).</summary>
        public static WorldGen.IDimensionPortals Portals;

        /// <summary>Client-only: parse every registered block's model and blockstate from the resource pack
        /// (<see cref="Block.LoadModel"/>). Run once after plugins load and before meshing. The headless server
        /// never calls this, so it loads no render assets and needs no resource pack.</summary>
        public static void LoadBlockModels()
        {
            foreach (var block in BlockRegistry.Values) block.LoadModel();
        }

        public static Block GetBlock(ushort id) => BlockRegistry[id];
        public static Block GetBlock(string key) => BlockRegistry[key];

        public static Item GetItem(ushort id) => ItemRegistry.TryGet(id, out var item) ? item : null;
        public static Item GetItem(string key) => ItemRegistry[key];
        public static bool TryGetItem(string key, out Item item) => ItemRegistry.TryGet(key, out item);
        public static IEnumerable<Item> Items => ItemRegistry.Values;

        /// <summary>The result of crafting the given N×N grid (row-major), or <see cref="ItemStack.Empty"/>
        /// if nothing matches. The first matching registered recipe wins.</summary>
        public static ItemStack MatchRecipe(ItemStack[] grid, int width, int height)
        {
            foreach (var recipe in RecipeRegistry.Values)
                if (recipe.Matches(grid, width, height))
                    return recipe.Result;
            return ItemStack.Empty;
        }

        internal static void RegisterRecipe(string prefix, CraftingRecipe recipe) =>
            RecipeRegistry.Register(prefix, recipe);

        /// <summary>The smelting recipe whose input the given stack satisfies, or null. First match wins.</summary>
        public static SmeltingRecipe MatchSmelting(ItemStack input)
        {
            if (input.IsEmpty) return null;
            foreach (var recipe in SmeltingRegistry.Values)
                if (recipe.Matches(input)) return recipe;
            return null;
        }

        internal static void RegisterSmelting(string prefix, SmeltingRecipe recipe) =>
            SmeltingRegistry.Register(prefix, recipe);

        public static Biome GetBiome(string key) => BiomeRegistry[key];
        public static Feature GetFeature(string key) => FeatureRegistry[key];
        public static Dimension GetDimension(string key) => DimensionRegistry[key];
        public static bool TryGetDimension(string key, out Dimension dimension) => DimensionRegistry.TryGet(key, out dimension);
        public static IEnumerable<Biome> Biomes => BiomeRegistry.Values;
        public static IEnumerable<Block> Blocks => BlockRegistry.Values;

        public static EntityType GetEntityType(ushort id) => EntityRegistry[id];
        public static EntityType GetEntityType(string key) => EntityRegistry[key];
        public static bool TryGetEntityType(ushort id, out EntityType type) => EntityRegistry.TryGet(id, out type);
        public static IEnumerable<EntityType> EntityTypes => EntityRegistry.Values;

        internal static string GetBlockDataRegistryKey(BlockData data)
        {
            var entry = new BlockDataRegistryEntry(data.GetType());
            return BlockDataRegistry[entry];
        }

        internal static string GetEntityDataRegistryKey(EntityData data)
            => EntityDataRegistry[new EntityDataRegistryEntry(data.GetType())];

        internal static System.Type GetEntityDataType(string key) => EntityDataRegistry[key].Type;
    }
}
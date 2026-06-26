using System.CodeDom;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using MinecraftClone3API.WorldGen;

namespace MinecraftClone3API.Plugins
{
    public class PluginContext
    {
        public readonly PluginAttribute PluginAttribute;

        internal readonly IPlugin Plugin;

        internal PluginContext(PluginAttribute pluginAttribute, IPlugin plugin)
        {
            PluginAttribute = pluginAttribute;
            Plugin = plugin;
        }

        /// <summary>Registers a block and its auto-generated <see cref="ItemBlock"/> (so every block is also
        /// an item, available in the inventory and placeable). Register the block before any recipe that
        /// references its item.</summary>
        public void Register(Block block)
        {
            GameRegistry.BlockRegistry.Register(PluginAttribute.Id, block);
            GameRegistry.ItemRegistry.Register(PluginAttribute.Id, new ItemBlock(block));
        }

        public void Register(Item item) => GameRegistry.ItemRegistry.Register(PluginAttribute.Id, item);

        public void Register(CraftingRecipe recipe) => GameRegistry.RecipeRegistry.Register(PluginAttribute.Id, recipe);
        public void Register<T>() where T : BlockData => GameRegistry.BlockDataRegistry.Register(PluginAttribute.Id, new BlockDataRegistryEntry(typeof(T)));
        public void RegisterEntityData<T>() where T : EntityData => GameRegistry.EntityDataRegistry.Register(PluginAttribute.Id, new EntityDataRegistryEntry(typeof(T)));
        public void Register(Biome biome) => GameRegistry.BiomeRegistry.Register(PluginAttribute.Id, biome);
        public void Register(Feature feature) => GameRegistry.FeatureRegistry.Register(PluginAttribute.Id, feature);
        public void Register(Dimension dimension) => GameRegistry.DimensionRegistry.Register(PluginAttribute.Id, dimension);
        public void Register(EntityType entityType) => GameRegistry.EntityRegistry.Register(PluginAttribute.Id, entityType);
    }
}

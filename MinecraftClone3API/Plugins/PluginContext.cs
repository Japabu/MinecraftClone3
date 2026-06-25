using System.CodeDom;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
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

        public void Register(Block block) => GameRegistry.BlockRegistry.Register(PluginAttribute.Id, block);
        public void Register<T>() where T : BlockData => GameRegistry.BlockDataRegistry.Register(PluginAttribute.Id, new BlockDataRegistryEntry(typeof(T)));
        public void Register(Biome biome) => GameRegistry.BiomeRegistry.Register(PluginAttribute.Id, biome);
        public void Register(Feature feature) => GameRegistry.FeatureRegistry.Register(PluginAttribute.Id, feature);
        public void Register(Dimension dimension) => GameRegistry.DimensionRegistry.Register(PluginAttribute.Id, dimension);
        public void Register(EntityType entityType) => GameRegistry.EntityRegistry.Register(PluginAttribute.Id, entityType);
    }
}

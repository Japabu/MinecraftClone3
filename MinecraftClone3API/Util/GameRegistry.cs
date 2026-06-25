using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.WorldGen;

namespace MinecraftClone3API.Util
{
    public static class GameRegistry
    {
        private const string RegistryFilename = "registry.bin";

        internal static readonly BlockRegistry BlockRegistry = new BlockRegistry();
        internal static readonly Registry<BlockDataRegistryEntry> BlockDataRegistry = new Registry<BlockDataRegistryEntry>();
        internal static readonly Registry<Biome> BiomeRegistry = new Registry<Biome>();
        internal static readonly Registry<Feature> FeatureRegistry = new Registry<Feature>();
        internal static readonly Registry<Dimension> DimensionRegistry = new Registry<Dimension>();

        public static List<string> GetMissingBlocks() => BlockRegistry.GetMissingBlocks();

        public static Block GetBlock(ushort id) => BlockRegistry[id];
        public static Block GetBlock(string key) => BlockRegistry[key];

        public static Biome GetBiome(string key) => BiomeRegistry[key];
        public static Feature GetFeature(string key) => FeatureRegistry[key];
        public static Dimension GetDimension(string key) => DimensionRegistry[key];
        public static bool TryGetDimension(string key, out Dimension dimension) => DimensionRegistry.TryGet(key, out dimension);
        public static IEnumerable<Biome> Biomes => BiomeRegistry.Values;
        public static IEnumerable<Block> Blocks => BlockRegistry.Values;

        internal static string GetBlockDataRegistryKey(BlockData data)
        {
            var entry = new BlockDataRegistryEntry(data.GetType());
            return BlockDataRegistry[entry];
        }

        public static void Save(DirectoryInfo saveDir)
        {
            var file = new FileInfo(Path.Combine(saveDir.FullName, RegistryFilename));

            using (var writer = new BinaryWriter(file.Create()))
            {
                BlockRegistry.Write(writer);
            }
        }

        public static void Load(DirectoryInfo saveDir)
        {
            var file = new FileInfo(Path.Combine(saveDir.FullName, RegistryFilename));
            if (!file.Exists) return;

            using (var reader = new BinaryReader(file.OpenRead()))
            {
                BlockRegistry.Read(reader);
            }
        }
    }
}
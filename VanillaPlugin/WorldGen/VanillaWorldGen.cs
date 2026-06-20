using MinecraftClone3API.Blocks;
using MinecraftClone3API.Plugins;
using MinecraftClone3API.Util;
using MinecraftClone3API.WorldGen;

namespace VanillaPlugin.WorldGen
{
    /// <summary>
    /// Registers the vanilla biomes and features and wires the shared ore veins onto the Overworld. Trees
    /// are per-biome (density varies); ores are dimension-shared (biome-independent) and attached in
    /// <see cref="AttachShared"/> during PostLoad, after every plugin's biomes/features exist.
    /// </summary>
    internal static class VanillaWorldGen
    {
        private static OreFeature _coal;
        private static OreFeature _iron;
        private static OreFeature _gold;
        private static OreFeature _diamond;

        public static void Register(PluginContext context)
        {
            var grass = GameRegistry.GetBlock("Vanilla:Grass");
            var dirt = GameRegistry.GetBlock("Vanilla:Dirt");
            var stone = GameRegistry.GetBlock("Vanilla:Stone");
            var sand = GameRegistry.GetBlock("Vanilla:Sand");
            var gravel = GameRegistry.GetBlock("Vanilla:Gravel");
            var snow = GameRegistry.GetBlock("Vanilla:Snow");
            var oakLog = GameRegistry.GetBlock("Vanilla:OakLog");
            var leaves = GameRegistry.GetBlock("Vanilla:Leaves");
            var coalOre = GameRegistry.GetBlock("Vanilla:CoalOre");
            var ironOre = GameRegistry.GetBlock("Vanilla:IronOre");
            var goldOre = GameRegistry.GetBlock("Vanilla:GoldOre");
            var diamondOre = GameRegistry.GetBlock("Vanilla:DiamondOre");

            var oakSparse = new TreeFeature("OakSparse", oakLog, leaves, 4, 6, 3, 0.6f);
            var oakDense = new TreeFeature("OakDense", oakLog, leaves, 4, 7, 9, 0.85f);
            context.Register(oakSparse);
            context.Register(oakDense);

            _coal = new OreFeature("CoalVein", coalOre, stone, 12, 18, 0, 70);
            _iron = new OreFeature("IronVein", ironOre, stone, 8, 12, -16, 50);
            _gold = new OreFeature("GoldVein", goldOre, stone, 6, 4, -28, 28);
            _diamond = new OreFeature("DiamondVein", diamondOre, stone, 5, 2, -32, -8);
            context.Register(_coal);
            context.Register(_iron);
            context.Register(_gold);
            context.Register(_diamond);

            context.Register(new Biome("Plains")
            {
                Temperature = 0.55f, Humidity = 0.45f,
                TopBlock = grass, FillerBlock = dirt, UnderwaterBlock = sand,
                HeightBias = 2, HeightVariation = 4
            }.InDimension(OverworldDimension.Key).AddFeature(DecorationStep.Vegetation, oakSparse));

            context.Register(new Biome("Forest")
            {
                Temperature = 0.5f, Humidity = 0.8f,
                TopBlock = grass, FillerBlock = dirt, UnderwaterBlock = sand,
                HeightBias = 3, HeightVariation = 5
            }.InDimension(OverworldDimension.Key).AddFeature(DecorationStep.Vegetation, oakDense));

            context.Register(new Biome("Desert")
            {
                Temperature = 0.92f, Humidity = 0.12f,
                TopBlock = sand, FillerBlock = sand, UnderwaterBlock = sand,
                HeightBias = 2, HeightVariation = 3
            }.InDimension(OverworldDimension.Key));

            context.Register(new Biome("Mountains")
            {
                Temperature = 0.22f, Humidity = 0.28f,
                TopBlock = grass, FillerBlock = dirt, UnderwaterBlock = gravel,
                HeightBias = 24, HeightVariation = 14
            }.InDimension(OverworldDimension.Key).AddFeature(DecorationStep.Vegetation, oakSparse));

            context.Register(new Biome("Snowy")
            {
                Temperature = 0.06f, Humidity = 0.55f,
                TopBlock = snow, FillerBlock = dirt, UnderwaterBlock = gravel,
                HeightBias = 3, HeightVariation = 5
            }.InDimension(OverworldDimension.Key).AddFeature(DecorationStep.Vegetation, oakSparse));

            context.Register(new Biome("Ocean")
            {
                ClimateSelectable = false,
                TopBlock = sand, FillerBlock = gravel, UnderwaterBlock = sand,
                HeightBias = -3, HeightVariation = 2
            }.InDimension(OverworldDimension.Key));

            context.Register(new Biome("Beach")
            {
                ClimateSelectable = false,
                TopBlock = sand, FillerBlock = sand, UnderwaterBlock = sand,
                HeightBias = 0, HeightVariation = 1
            }.InDimension(OverworldDimension.Key));
        }

        public static void AttachShared()
        {
            var overworld = GameRegistry.GetDimension(OverworldDimension.Key);
            overworld.AddFeature(DecorationStep.Ores, _coal);
            overworld.AddFeature(DecorationStep.Ores, _iron);
            overworld.AddFeature(DecorationStep.Ores, _gold);
            overworld.AddFeature(DecorationStep.Ores, _diamond);
        }
    }
}

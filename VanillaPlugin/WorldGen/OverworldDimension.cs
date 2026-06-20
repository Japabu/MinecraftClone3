using System.Collections.Generic;
using System.Linq;
using MinecraftClone3API.Util;
using MinecraftClone3API.WorldGen;

namespace VanillaPlugin.WorldGen
{
    /// <summary>
    /// The vanilla Overworld. Owns the noise + content wiring for a temperate, ocean-and-continents world.
    /// Its climate biome source is built from <em>every</em> registered biome tagged <see cref="Key"/>, so a
    /// third-party plugin's overworld biome participates without touching this class; ocean and beach are
    /// resolved explicitly because they're height-derived, not climate-selected.
    /// </summary>
    public class OverworldDimension : Dimension
    {
        public const string Key = "Vanilla:Overworld";

        public OverworldDimension() : base("Overworld")
        {
        }

        public override IChunkGenerator CreateGenerator(long seed)
        {
            var stone = GameRegistry.GetBlock("Vanilla:Stone");
            var water = GameRegistry.GetBlock("Vanilla:Water");
            var bedrock = GameRegistry.GetBlock("Vanilla:Bedrock");
            var ocean = GameRegistry.GetBiome("Vanilla:Ocean");
            var beach = GameRegistry.GetBiome("Vanilla:Beach");

            var landBiomes = GameRegistry.Biomes.Where(b => b.BelongsTo(Key) && b.ClimateSelectable);
            var source = new ClimateBiomeSource(landBiomes);
            var carvers = new List<Carver> {new NoiseCaveCarver(seed)};

            return new NoiseChunkGenerator(seed, this, source, ocean, beach, stone, water, bedrock, carvers);
        }
    }
}

using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// Pure data describing one biome: where it sits in climate space, the blocks that skin its surface,
    /// how it bends the terrain height, and the features it decorates with. The engine bakes no vanilla
    /// values here — a plugin fills these in and registers the biome, tagging the dimension(s) it belongs
    /// to so a <see cref="ClimateBiomeSource"/> picks it up automatically.
    /// </summary>
    public class Biome : RegistryEntry
    {
        /// <summary>Climate point in [0,1]². The climate biome source picks the nearest registered biome.</summary>
        public float Temperature;
        public float Humidity;

        /// <summary>Surface skin: the top block (e.g. grass), the filler below it (e.g. dirt), and the
        /// block used where the column lies below sea level (e.g. sand sea floor).</summary>
        public Block TopBlock;
        public Block FillerBlock;
        public Block UnderwaterBlock;
        public int TopDepth = 1;
        public int FillerDepth = 4;

        /// <summary>Added to the column's base terrain height; <see cref="HeightVariation"/> scales the
        /// fine "peaks" noise. A mountains biome carries a large bias; flat biomes carry ~0.</summary>
        public float HeightBias;
        public float HeightVariation = 6;

        /// <summary>False for height-derived biomes (ocean/beach) the climate source must not pick.</summary>
        public bool ClimateSelectable = true;

        public readonly List<string> Dimensions = new List<string>();

        private readonly Dictionary<DecorationStep, List<Feature>> _features =
            new Dictionary<DecorationStep, List<Feature>>();

        public Biome(string name) : base(name)
        {
        }

        public Biome InDimension(string dimensionKey)
        {
            Dimensions.Add(dimensionKey);
            return this;
        }

        public bool BelongsTo(string dimensionKey) => Dimensions.Contains(dimensionKey);

        public Biome AddFeature(DecorationStep step, Feature feature)
        {
            if (!_features.TryGetValue(step, out var list))
            {
                list = new List<Feature>();
                _features[step] = list;
            }

            list.Add(feature);
            return this;
        }

        private static readonly Feature[] NoFeatures = new Feature[0];

        public IReadOnlyList<Feature> GetFeatures(DecorationStep step)
            => _features.TryGetValue(step, out var list) ? list : NoFeatures;
    }
}

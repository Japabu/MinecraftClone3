using System.Collections.Generic;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// A world type the engine can bind a <see cref="MinecraftClone3API.Blocks.WorldServer"/> to.
    /// <see cref="CreateGenerator"/> builds a fresh seeded generator (called once, after every plugin's
    /// PostLoad, so the registry is complete). The shared per-step feature lists are the cross-plugin
    /// decoration hook: any plugin can <see cref="AddFeature"/> to a dimension it doesn't own.
    /// </summary>
    public abstract class Dimension : RegistryEntry
    {
        private readonly Dictionary<DecorationStep, List<Feature>> _features =
            new Dictionary<DecorationStep, List<Feature>>();

        protected Dimension(string name) : base(name)
        {
        }

        public void AddFeature(DecorationStep step, Feature feature)
        {
            if (!_features.TryGetValue(step, out var list))
            {
                list = new List<Feature>();
                _features[step] = list;
            }

            list.Add(feature);
        }

        private static readonly Feature[] NoFeatures = new Feature[0];

        public IReadOnlyList<Feature> GetFeatures(DecorationStep step)
            => _features.TryGetValue(step, out var list) ? list : NoFeatures;

        public abstract IChunkGenerator CreateGenerator(long seed);
    }
}

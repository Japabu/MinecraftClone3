using System.Collections.Generic;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

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

        /// <summary>Generic per-dimension visuals the client applies when a player is in this dimension (shipped
        /// in the dimension-change handshake). The engine has no per-dimension sky knowledge — content sets these.
        /// <see cref="HasSky"/> false drops the sun/day-night and stars and paints a flat <see cref="FogColor"/>;
        /// <see cref="AmbientLight"/> is a minimum light flooded everywhere so a sunless dimension isn't pitch
        /// black. Defaults are the open-sky Overworld (sky on, no fog override, no ambient floor).</summary>
        public bool HasSky = true;
        public Vector3 FogColor = Vector3.Zero;
        public Vector3 AmbientLight = Vector3.Zero;

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

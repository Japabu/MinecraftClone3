using System.Collections.Generic;
using System.Linq;

namespace MinecraftClone3API.WorldGen
{
    public abstract class BiomeSource
    {
        /// <param name="temperature">Climate temperature in [0,1].</param>
        /// <param name="humidity">Climate humidity in [0,1].</param>
        public abstract Biome Get(float temperature, float humidity);
    }

    /// <summary>
    /// Picks the climate-nearest biome (Voronoi in temperature/humidity space) from a fixed member set.
    /// The members are the climate-selectable biomes a dimension enumerates from the registry at
    /// generator-creation time, so any plugin's biome tagged for that dimension automatically participates.
    /// </summary>
    public class ClimateBiomeSource : BiomeSource
    {
        private readonly Biome[] _biomes;

        public ClimateBiomeSource(IEnumerable<Biome> biomes)
        {
            _biomes = biomes.Where(b => b.ClimateSelectable).ToArray();
        }

        public bool HasMembers => _biomes.Length > 0;

        public override Biome Get(float temperature, float humidity)
        {
            Biome best = null;
            var bestDistance = float.MaxValue;
            foreach (var biome in _biomes)
            {
                var dt = biome.Temperature - temperature;
                var dh = biome.Humidity - humidity;
                var distance = dt * dt + dh * dh;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = biome;
            }

            return best;
        }
    }
}

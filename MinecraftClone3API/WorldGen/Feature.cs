using MinecraftClone3API.Util;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// A decoration primitive (ore vein, tree, patch, …) placed during a <see cref="DecorationStep"/>.
    /// <see cref="Place"/> runs once per origin chunk it could reach, with its own deterministic
    /// <see cref="WorldGenRandom"/> stream (seeded partly from <see cref="Salt"/>), and writes through the
    /// region — which clips to the chunk being generated. A feature must keep its reach within ~one chunk
    /// of <paramref name="originColumn"/> so the ±1-chunk decoration margin covers it.
    /// </summary>
    public abstract class Feature : RegistryEntry
    {
        protected Feature(string name) : base(name)
        {
        }

        /// <summary>Process-stable per-feature seed offset, so adding or removing one feature does not
        /// perturb another's placements for the same seed.</summary>
        public int Salt => WorldGenRandom.StableHash(RegistryKey);

        /// <param name="originColumn">World coordinate of the origin chunk's (x,0,z) min corner.</param>
        public abstract void Place(IChunkGenRegion region, Vector3i originColumn, ref WorldGenRandom rng);
    }
}

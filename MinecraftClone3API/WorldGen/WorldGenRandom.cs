namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// Allocation-free deterministic PRNG (SplitMix64) used by decoration. Seeded from
    /// (worldSeed, originChunkX, originChunkZ, featureSalt) so the same origin chunk produces the same
    /// placements no matter which neighbour is being generated — that, plus clipping writes to the
    /// current chunk, is what makes cross-chunk features (a tree straddling a border) consistent.
    /// It is a struct passed by <c>ref</c> so a feature's draws advance one shared stream with no heap churn.
    /// </summary>
    public struct WorldGenRandom
    {
        private ulong _state;

        public WorldGenRandom(long seed, int originChunkX, int originChunkZ, int featureSalt)
        {
            _state = (ulong) seed;
            _state = Mix(_state ^ ((ulong) (uint) originChunkX * 0x9E3779B97F4A7C15UL));
            _state = Mix(_state ^ ((ulong) (uint) originChunkZ * 0xC2B2AE3D27D4EB4FUL));
            _state = Mix(_state ^ ((ulong) (uint) featureSalt * 0x165667B19E3779F9UL));
        }

        private static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public ulong NextULong()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform in [0, bound). <paramref name="bound"/> must be positive.</summary>
        public int NextInt(int bound) => (int) (NextULong() % (ulong) bound);

        /// <summary>Uniform in [min, maxInclusive].</summary>
        public int NextInt(int min, int maxInclusive) => min + NextInt(maxInclusive - min + 1);

        /// <summary>Uniform in [0, 1).</summary>
        public float NextFloat() => (NextULong() >> 40) * (1.0f / 16777216.0f);

        /// <summary>Process-stable 32-bit hash (FNV-1a). <see cref="System.String.GetHashCode"/> is
        /// randomized per run, so a feature's salt must come from this to keep a seed reproducible.</summary>
        public static int StableHash(string text)
        {
            unchecked
            {
                var hash = (uint) 2166136261;
                foreach (var c in text)
                {
                    hash ^= c;
                    hash *= 16777619;
                }

                return (int) hash;
            }
        }
    }
}

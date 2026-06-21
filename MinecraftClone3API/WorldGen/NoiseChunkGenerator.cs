using System;
using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// The reusable noise-driven generator. Per chunk it runs, in order: biome + surface-height map →
    /// base terrain (bedrock/stone) → biome surface skin → water below sea level → carvers →
    /// gen-time sky seeding → decoration (features, with a ±1-chunk margin and per-origin population seed).
    /// All content blocks (stone/water/bedrock) and biomes are injected by the dimension, so this class
    /// holds no vanilla knowledge. <see cref="Generate"/> is THREAD-SAFE (the load thread fans a batch across
    /// cores): every field is read-only/pure except the column scratch, which is per-thread (ThreadLocal).
    /// </summary>
    public class NoiseChunkGenerator : IChunkGenerator
    {
        public int SeaLevel = 8;
        public int BedrockY = -32;
        public int WorldTop = 96;
        public int HeightBlendSpacing = 24;

        private const float ContinentalScale = 0.0035f;
        private const float HillScale = 0.013f;
        private const float PeakScale = 0.05f;
        private const float ClimateScale = 0.0042f;
        private const float ContinentalAmp = 18f;
        private const float HillAmp = 7f;
        private const float LandBias = 4f;
        private const int OceanThreshold = 4;
        private const int BeachBand = 1;
        private const int DecorationHeadroom = 12;

        private static readonly DecorationStep[] Steps = {DecorationStep.Ores, DecorationStep.Vegetation};

        private readonly long _seed;
        private readonly Dimension _dimension;
        private readonly BiomeSource _biomeSource;
        private readonly Biome _ocean;
        private readonly Biome _beach;
        private readonly Block _stone;
        private readonly Block _water;
        private readonly Block _bedrock;
        private readonly List<Carver> _carvers;

        private readonly OpenSimplexNoise _continental;
        private readonly OpenSimplexNoise _hills;
        private readonly OpenSimplexNoise _peaks;
        private readonly OpenSimplexNoise _temperature;
        private readonly OpenSimplexNoise _humidity;

        // Per-THREAD scratch so the server load thread can generate a batch of chunks in PARALLEL across cores
        // (gen is embarrassingly parallel — every other field here is read-only/pure: the noise perm tables,
        // the biome source, the carvers/features). Each thread reuses its own arrays (no per-chunk alloc).
        private readonly System.Threading.ThreadLocal<int[]> _surfScratch =
            new System.Threading.ThreadLocal<int[]>(() => new int[Chunk.Size * Chunk.Size]);
        private readonly System.Threading.ThreadLocal<Biome[]> _colBiomeScratch =
            new System.Threading.ThreadLocal<Biome[]>(() => new Biome[Chunk.Size * Chunk.Size]);

        public NoiseChunkGenerator(long seed, Dimension dimension, BiomeSource biomeSource, Biome ocean, Biome beach,
            Block stone, Block water, Block bedrock, List<Carver> carvers)
        {
            _seed = seed;
            _dimension = dimension;
            _biomeSource = biomeSource;
            _ocean = ocean;
            _beach = beach;
            _stone = stone;
            _water = water;
            _bedrock = bedrock;
            _carvers = carvers ?? new List<Carver>();

            _continental = new OpenSimplexNoise(seed ^ 0x00C0FFEE00000001L);
            _hills = new OpenSimplexNoise(seed ^ 0x00C0FFEE00000002L);
            _peaks = new OpenSimplexNoise(seed ^ 0x00C0FFEE00000003L);
            _temperature = new OpenSimplexNoise(seed ^ 0x00C0FFEE00000004L);
            _humidity = new OpenSimplexNoise(seed ^ 0x00C0FFEE00000005L);
        }

        public int MinChunkY => FloorDiv(BedrockY, Chunk.Size);
        public int MaxChunkY => FloorDiv(WorldTop + Chunk.Size - 1, Chunk.Size);

        private float BaseHeight(int wx, int wz)
        {
            var c = _continental.Generate(wx * ContinentalScale, wz * ContinentalScale);
            var h = _hills.Generate(wx * HillScale, wz * HillScale);
            return SeaLevel + LandBias + c * ContinentalAmp + h * HillAmp;
        }

        public Biome BiomeAt(int wx, int wz)
        {
            var bh = BaseHeight(wx, wz);
            if (bh < SeaLevel - OceanThreshold) return _ocean;
            if (bh <= SeaLevel + BeachBand) return _beach;

            var t = _temperature.Generate(wx * ClimateScale, wz * ClimateScale) * 0.5f + 0.5f;
            var hum = _humidity.Generate(wx * ClimateScale, wz * ClimateScale) * 0.5f + 0.5f;
            return _biomeSource.Get(t, hum) ?? _beach;
        }

        public int SurfaceHeight(int wx, int wz)
        {
            BlendedHeightParams(wx, wz, out var bias, out var variation);
            var bh = BaseHeight(wx, wz);
            var p = _peaks.Generate(wx * PeakScale, wz * PeakScale);
            var s = (int) MathF.Round(bh + bias + p * variation);
            if (s < BedrockY + 1) s = BedrockY + 1;
            if (s > WorldTop) s = WorldTop;
            return s;
        }

        /// <summary>
        /// Bilinearly blends the four surrounding biomes' <see cref="Biome.HeightBias"/>/
        /// <see cref="Biome.HeightVariation"/> over a world-aligned lattice of spacing
        /// <see cref="HeightBlendSpacing"/> (smoothstep weights), so terrain height crosses a biome border
        /// as a foothill instead of a cliff. A pure function of (wx,wz), so every height consumer agrees.
        /// </summary>
        private void BlendedHeightParams(int wx, int wz, out float bias, out float variation)
        {
            var s = HeightBlendSpacing;
            var x0 = FloorDiv(wx, s) * s;
            var z0 = FloorDiv(wz, s) * s;
            var x1 = x0 + s;
            var z1 = z0 + s;

            var tx = (wx - x0) / (float) s;
            var tz = (wz - z0) / (float) s;
            tx = tx * tx * (3f - 2f * tx);
            tz = tz * tz * (3f - 2f * tz);

            var b00 = BiomeAt(x0, z0);
            var b10 = BiomeAt(x1, z0);
            var b01 = BiomeAt(x0, z1);
            var b11 = BiomeAt(x1, z1);

            bias = Lerp(Lerp(b00.HeightBias, b10.HeightBias, tx),
                Lerp(b01.HeightBias, b11.HeightBias, tx), tz);
            variation = Lerp(Lerp(b00.HeightVariation, b10.HeightVariation, tx),
                Lerp(b01.HeightVariation, b11.HeightVariation, tx), tz);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        public void Generate(CachedChunk chunk, Vector3i chunkPos)
        {
            var min = chunkPos * Chunk.Size;
            var surfMap = _surfScratch.Value;
            var biomeMap = _colBiomeScratch.Value;

            var colTopMax = int.MinValue;
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var idx = x * Chunk.Size + z;
                var biome = BiomeAt(min.X + x, min.Z + z);
                var surf = SurfaceHeight(min.X + x, min.Z + z);
                biomeMap[idx] = biome;
                surfMap[idx] = surf;
                if (surf > colTopMax) colTopMax = surf;
            }

            // All-air fast path: a chunk whose bottom sits above every surface column (+ the decoration
            // headroom that bounds tree tops) and above sea level produces no blocks — the fill/carve/sky
            // passes below would iterate 4096 cells twice to set nothing, decoration is gated off, and the
            // chunk is then discarded as IsEmpty anyway. At high render distance the vertical band is mostly
            // this empty air above the terrain (≈¾ of generated chunks), so skipping it is the single biggest
            // gen-throughput win. (Seeding sky light is pointless for a discarded chunk — the client falls
            // back to sky 15 for any unloaded chunk; see WorldClient.GetSkyLight.)
            if (min.Y > SeaLevel && min.Y > colTopMax + DecorationHeadroom)
                return;

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var idx = x * Chunk.Size + z;
                var surf = surfMap[idx];
                var biome = biomeMap[idx];
                var ocean = surf < SeaLevel;

                for (var ly = 0; ly < Chunk.Size; ly++)
                {
                    var wy = min.Y + ly;
                    Block block = null;

                    if (wy <= surf)
                    {
                        if (wy == BedrockY) block = _bedrock;
                        else
                        {
                            var depth = surf - wy;
                            if (ocean)
                                block = depth < biome.TopDepth + biome.FillerDepth
                                    ? biome.UnderwaterBlock ?? _stone
                                    : _stone;
                            else if (depth < biome.TopDepth) block = biome.TopBlock ?? _stone;
                            else if (depth < biome.TopDepth + biome.FillerDepth) block = biome.FillerBlock ?? _stone;
                            else block = _stone;
                        }
                    }
                    else if (ocean && wy <= SeaLevel)
                    {
                        block = _water;
                    }

                    if (block != null) chunk.SetBlock(x, ly, z, block);
                }
            }

            foreach (var carver in _carvers) carver.Carve(chunk, chunkPos, this, surfMap);

            // Seed sky light above the solid surface. Open air is full sky; the water column dims one
            // level per block of depth so the surface is bright and the deep is dark (no BFS at gen).
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var idx = x * Chunk.Size + z;
                var surf = surfMap[idx];
                var ocean = surf < SeaLevel;

                for (var ly = 0; ly < Chunk.Size; ly++)
                {
                    var wy = min.Y + ly;
                    if (wy <= surf) continue;

                    var sky = ocean && wy <= SeaLevel
                        ? Math.Max(0, LightLevel.SkyMax - (SeaLevel - wy))
                        : LightLevel.SkyMax;
                    if (sky > 0) chunk.SetSkyLight(x, ly, z, sky);
                }
            }

            if (min.Y <= colTopMax + DecorationHeadroom) Decorate(chunk, chunkPos);
        }

        private void Decorate(CachedChunk chunk, Vector3i chunkPos)
        {
            var region = new ChunkGenRegion(this, chunk, chunkPos);

            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                var ocx = chunkPos.X + dx;
                var ocz = chunkPos.Z + dz;
                var originColumn = new Vector3i(ocx * Chunk.Size, 0, ocz * Chunk.Size);
                var centerBiome = BiomeAt(ocx * Chunk.Size + Chunk.Size / 2, ocz * Chunk.Size + Chunk.Size / 2);

                foreach (var step in Steps)
                {
                    var shared = _dimension?.GetFeatures(step);
                    if (shared != null)
                        for (var i = 0; i < shared.Count; i++)
                            RunFeature(shared[i], region, originColumn, ocx, ocz);

                    var biomeFeatures = centerBiome.GetFeatures(step);
                    for (var i = 0; i < biomeFeatures.Count; i++)
                        RunFeature(biomeFeatures[i], region, originColumn, ocx, ocz);
                }
            }
        }

        private void RunFeature(Feature feature, IChunkGenRegion region, Vector3i originColumn, int ocx, int ocz)
        {
            var rng = new WorldGenRandom(_seed, ocx, ocz, feature.Salt);
            feature.Place(region, originColumn, ref rng);
        }

        public Vector3i Spawn()
        {
            for (var r = 0; r <= 64; r++)
            for (var dx = -r; dx <= r; dx++)
            for (var dz = -r; dz <= r; dz++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;

                var biome = BiomeAt(dx, dz);
                if (biome == _ocean) continue;

                var surf = SurfaceHeight(dx, dz);
                if (surf >= SeaLevel) return new Vector3i(dx, surf + 2, dz);
            }

            return new Vector3i(0, SeaLevel + 2, 0);
        }

        private static int FloorDiv(int a, int b)
        {
            var q = a / b;
            if (a % b != 0 && (a < 0) != (b < 0)) q--;
            return q;
        }
    }
}

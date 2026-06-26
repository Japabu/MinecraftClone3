using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using MinecraftClone3API.WorldGen;
using OpenTK.Mathematics;

namespace VanillaPlugin.WorldGen
{
    /// <summary>
    /// The Nether generator: a 128-tall slab of netherrack between a bedrock floor (y 0) and ceiling (y 127),
    /// hollowed by 3D-noise caverns, with a lava sea filling everything below <see cref="LavaLevel"/>. Soul-sand
    /// patches skin cavern floors and glowstone clusters hang from cavern ceilings. There is NO sky: the
    /// generator never seeds sky light, so the sealed slab stays dark (lit only by lava/glowstone/portals).
    /// <see cref="Generate"/> is thread-safe — every field is read-only/pure (the load thread fans chunks across
    /// cores).
    /// </summary>
    public class NetherChunkGenerator : IChunkGenerator
    {
        public const int FloorY = 0;
        public const int CeilingY = 127;
        public const int LavaLevel = 31;

        private const int FloorRough = 4;     // y 1..4: bedrock/netherrack speckle above the floor
        private const int CeilRough = 123;    // y 123..126: bedrock/netherrack speckle below the ceiling

        private const float CaveScaleXZ = 0.018f;
        private const float CaveScaleY = 0.030f;
        private const float CaveThreshold = 0.28f;
        private const float DecoScale = 0.08f;

        private readonly long _seed;
        private readonly Block _netherrack;
        private readonly Block _lava;
        private readonly Block _bedrock;
        private readonly Block _soulSand;
        private readonly Block _glowstone;

        private readonly OpenSimplexNoise _cave;
        private readonly OpenSimplexNoise _rough;
        private readonly OpenSimplexNoise _deco;

        public NetherChunkGenerator(long seed, Block netherrack, Block lava, Block bedrock, Block soulSand,
            Block glowstone)
        {
            _seed = seed;
            _netherrack = netherrack;
            _lava = lava;
            _bedrock = bedrock;
            _soulSand = soulSand;
            _glowstone = glowstone;

            _cave = new OpenSimplexNoise(seed ^ 0x000E7E40A0000001L);
            _rough = new OpenSimplexNoise(seed ^ 0x000E7E40A0000002L);
            _deco = new OpenSimplexNoise(seed ^ 0x000E7E40A0000003L);
        }

        public int MinChunkY => 0;
        public int MaxChunkY => CeilingY / Chunk.Size;   // 7 -> covers y 0..127

        /// <summary>True if the cell is carved-open cavern (not solid netherrack), ignoring the bedrock
        /// shell. Pure: used for both fill and decoration neighbour tests so no cross-chunk reads.</summary>
        private bool IsOpen(int wx, int wy, int wz)
        {
            var d = _cave.Generate(wx * CaveScaleXZ, wy * CaveScaleY, wz * CaveScaleXZ);
            return d > CaveThreshold;
        }

        /// <summary>The base block (no decoration) at a world cell, or null for air. Pure.</summary>
        private Block BaseAt(int wx, int wy, int wz)
        {
            if (wy < FloorY || wy > CeilingY) return null;
            if (wy == FloorY || wy == CeilingY) return _bedrock;

            if (wy <= FloorRough || wy >= CeilRough)
            {
                var r = _rough.Generate(wx * 0.6f, wy * 0.6f, wz * 0.6f);
                var bias = wy <= FloorRough ? (FloorRough - wy) : (wy - CeilRough);
                return r > 0.55f - bias * 0.25f ? _bedrock : _netherrack;
            }

            if (!IsOpen(wx, wy, wz)) return _netherrack;
            return wy <= LavaLevel ? _lava : null;
        }

        public void Generate(CachedChunk chunk, Vector3i chunkPos)
        {
            var min = chunkPos * Chunk.Size;
            if (min.Y > CeilingY || min.Y + Chunk.Size <= FloorY) return;   // outside the slab → empty, no sky seed

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var wx = min.X + x;
                var wz = min.Z + z;
                for (var y = 0; y < Chunk.Size; y++)
                {
                    var wy = min.Y + y;
                    var block = BaseAt(wx, wy, wz);
                    if (block == _netherrack)
                    {
                        // Soul-sand skin on cavern floors; glowstone clusters on cavern ceilings.
                        if (BaseAt(wx, wy + 1, wz) == null && wy > LavaLevel &&
                            _deco.Generate(wx * DecoScale, 9.0f, wz * DecoScale) > 0.55f)
                            block = _soulSand;
                        else if (BaseAt(wx, wy - 1, wz) == null && wy > LavaLevel + 6 &&
                                 _deco.Generate(wx * DecoScale, -4.0f, wz * DecoScale) > 0.72f)
                            block = _glowstone;
                    }

                    if (block != null) chunk.SetBlock(x, y, z, block);
                }
            }
        }

        public long GetLodColumn(int wx, int wz) => 0;

        public void DecorateLodRegion(Vector3i regionKey, long[] columns) { }

        public Vector3i Spawn() => new Vector3i(0, LavaLevel + 16, 0);
    }
}

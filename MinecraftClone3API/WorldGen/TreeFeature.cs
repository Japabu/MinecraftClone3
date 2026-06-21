using System;
using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// A simple round-canopy tree (oak shape): a vertical trunk topped by a leaf blob. Plants on the noise
    /// surface of each attempt column that is above sea level; leaves only fill air (never overwrite the
    /// trunk or terrain). Trunk and canopy are emitted in absolute coordinates, so a tree whose base sits
    /// in a neighbour chunk still drops the leaves that overhang into the chunk being generated.
    /// </summary>
    public class TreeFeature : Feature
    {
        private readonly Block _log;
        private readonly Block _leaves;
        private readonly int _minHeight;
        private readonly int _maxHeight;
        private readonly int _attemptsPerChunk;
        private readonly float _chance;

        public TreeFeature(string name, Block log, Block leaves, int minHeight, int maxHeight,
            int attemptsPerChunk, float chance) : base(name)
        {
            _log = log;
            _leaves = leaves;
            _minHeight = minHeight;
            _maxHeight = maxHeight;
            _attemptsPerChunk = attemptsPerChunk;
            _chance = chance;
        }

        /// <summary>The leaf block this feature's canopy is made of — the LOD horizon stamps it as the surface.</summary>
        public Block Leaves => _leaves;

        /// <summary>Collects the trunk position + canopy-top Y of every tree this feature places for the given
        /// origin chunk (no chunk writes), drawing the RNG in the EXACT order <see cref="Place"/> does
        /// (chance → x → z → [sea-level skip] → height), so the stream stays bit-identical to full-chunk gen and
        /// the LOD canopy lands on the same columns the real trees do. Used by the horizon LOD to show trees.</summary>
        public void CollectTrees(IChunkGenRegion region, Vector3i originColumn, ref WorldGenRandom rng,
            List<(int X, int Z, int TopY)> into)
        {
            for (var a = 0; a < _attemptsPerChunk; a++)
            {
                if (rng.NextFloat() > _chance) continue;
                var x = originColumn.X + rng.NextInt(Chunk.Size);
                var z = originColumn.Z + rng.NextInt(Chunk.Size);
                var surf = region.SurfaceHeight(x, z);
                if (surf < region.SeaLevel) continue;
                var height = rng.NextInt(_minHeight, _maxHeight);
                into.Add((x, z, surf + height));
            }
        }

        public override void Place(IChunkGenRegion region, Vector3i originColumn, ref WorldGenRandom rng)
        {
            for (var a = 0; a < _attemptsPerChunk; a++)
            {
                if (rng.NextFloat() > _chance) continue;

                var x = originColumn.X + rng.NextInt(Chunk.Size);
                var z = originColumn.Z + rng.NextInt(Chunk.Size);
                var surf = region.SurfaceHeight(x, z);
                if (surf < region.SeaLevel) continue;

                var height = rng.NextInt(_minHeight, _maxHeight);
                var topY = surf + height;

                for (var y = surf + 1; y <= topY; y++)
                    region.SetBlock(x, y, z, _log);

                for (var y = topY - 2; y <= topY + 1; y++)
                {
                    var radius = y <= topY - 1 ? 2 : 1;
                    for (var dx = -radius; dx <= radius; dx++)
                    for (var dz = -radius; dz <= radius; dz++)
                    {
                        if (radius == 2 && Math.Abs(dx) == 2 && Math.Abs(dz) == 2) continue;
                        if (dx == 0 && dz == 0 && y <= topY) continue;

                        var lx = x + dx;
                        var lz = z + dz;
                        if (region.GetBlock(lx, y, lz) == BlockRegistry.BlockAir)
                            region.SetBlock(lx, y, lz, _leaves);
                    }
                }
            }
        }
    }
}

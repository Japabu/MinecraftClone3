using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// Scatters a single-block plant (grass tuft, fern, flower) in small clusters over the noise surface. Each
    /// attempt seeds a patch centre, then drops a few plants in a small radius around it onto exposed
    /// <see cref="_soil"/> columns above sea level (skipping water and occupied cells). Reach is bounded by
    /// <see cref="_patchRadius"/> so it stays within the ±1-chunk decoration margin.
    /// </summary>
    public class PatchFeature : Feature
    {
        private readonly Block _plant;
        private readonly Block _soil;
        private readonly int _patches;
        private readonly int _patchRadius;
        private readonly int _perPatch;

        public PatchFeature(string name, Block plant, Block soil, int patches, int patchRadius, int perPatch)
            : base(name)
        {
            _plant = plant;
            _soil = soil;
            _patches = patches;
            _patchRadius = patchRadius;
            _perPatch = perPatch;
        }

        public override void Place(IChunkGenRegion region, Vector3D<int> originColumn, ref WorldGenRandom rng)
        {
            for (var p = 0; p < _patches; p++)
            {
                var cx = originColumn.X + rng.NextInt(Chunk.Size);
                var cz = originColumn.Z + rng.NextInt(Chunk.Size);
                for (var i = 0; i < _perPatch; i++)
                {
                    var x = cx + rng.NextInt(-_patchRadius, _patchRadius);
                    var z = cz + rng.NextInt(-_patchRadius, _patchRadius);
                    var surf = region.SurfaceHeight(x, z);
                    if (surf < region.SeaLevel) continue;
                    if (region.GetBlock(x, surf, z) != _soil) continue;
                    if (region.GetBlock(x, surf + 1, z) != BlockRegistry.BlockAir) continue;
                    region.SetBlock(x, surf + 1, z, _plant);
                }
            }
        }
    }
}

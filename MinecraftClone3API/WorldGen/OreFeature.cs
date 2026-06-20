using MinecraftClone3API.Blocks;
using OpenTK.Mathematics;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// Scatters compact veins of an ore block, replacing a target block (usually stone) within a Y band.
    /// Each vein is a small random blob around a seed cell; cells that aren't the target (air, other ore,
    /// already carved) are left alone, and writes outside the current chunk are dropped by the region.
    /// </summary>
    public class OreFeature : Feature
    {
        private readonly Block _ore;
        private readonly Block _target;
        private readonly int _veinSize;
        private readonly int _veinsPerChunk;
        private readonly int _minY;
        private readonly int _maxY;

        public OreFeature(string name, Block ore, Block target, int veinSize, int veinsPerChunk, int minY, int maxY)
            : base(name)
        {
            _ore = ore;
            _target = target;
            _veinSize = veinSize;
            _veinsPerChunk = veinsPerChunk;
            _minY = minY;
            _maxY = maxY;
        }

        public override void Place(IChunkGenRegion region, Vector3i originColumn, ref WorldGenRandom rng)
        {
            for (var v = 0; v < _veinsPerChunk; v++)
            {
                var cx = originColumn.X + rng.NextInt(Chunk.Size);
                var cz = originColumn.Z + rng.NextInt(Chunk.Size);
                var cy = rng.NextInt(_minY, _maxY);

                for (var i = 0; i < _veinSize; i++)
                {
                    var px = cx + rng.NextInt(-1, 1);
                    var py = cy + rng.NextInt(-1, 1);
                    var pz = cz + rng.NextInt(-1, 1);

                    if (region.GetBlock(px, py, pz) == _target)
                        region.SetBlock(px, py, pz, _ore);
                }
            }
        }
    }
}

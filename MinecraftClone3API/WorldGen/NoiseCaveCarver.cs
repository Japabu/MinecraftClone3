using System;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// "Spaghetti" cave carver: two decorrelated 3D noise fields, carved where both cross near zero, giving
    /// winding tubes. Only carves solid cells strictly below the surface skin and above the bedrock floor,
    /// so it never breaches the surface skin wholesale nor removes the world floor. Carved cells become air
    /// and keep sky-light 0 (dark caves).
    /// </summary>
    public class NoiseCaveCarver : Carver
    {
        private readonly OpenSimplexNoise _a;
        private readonly OpenSimplexNoise _b;

        private const float Scale = 0.045f;
        private const float VerticalScale = 0.06f;
        private const float Threshold = 0.022f;
        private const int SurfaceBuffer = 3;

        public NoiseCaveCarver(long seed)
        {
            _a = new OpenSimplexNoise(seed ^ 0x53A1C0DEBA5EBA11L);
            _b = new OpenSimplexNoise(seed ^ 0x1F123BB5C0FFEE42L);
        }

        public override void Carve(CachedChunk chunk, Vector3D<int> chunkPos, NoiseChunkGenerator generator,
            int[] surfaceHeights)
        {
            var min = chunkPos * Chunk.Size;
            var floor = generator.BedrockY + 1;

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var wx = min.X + x;
                var wz = min.Z + z;
                var top = Math.Min(surfaceHeights[x * Chunk.Size + z] - SurfaceBuffer, min.Y + Chunk.Size - 1);

                for (var wy = Math.Max(min.Y, floor); wy <= top; wy++)
                {
                    var ly = wy - min.Y;
                    if (chunk.GetBlock(x, ly, z) == BlockRegistry.BlockAir) continue;

                    var a = _a.Generate(wx * Scale, wy * VerticalScale, wz * Scale);
                    var b = _b.Generate(wx * Scale, wy * VerticalScale, wz * Scale);
                    if (a * a + b * b < Threshold)
                        chunk.SetBlock(x, ly, z, BlockRegistry.BlockAir);
                }
            }
        }
    }
}

using System;
using Silk.NET.Maths;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Conversions between the integer block-coordinate vector (<c>Vector3i</c> = Silk.NET.Maths
    /// <c>Vector3D&lt;int&gt;</c>) and the float vector. The block containing a point is its <em>floor</em>, not a
    /// truncation toward zero: <c>(int)(-0.5f)</c> is <c>0</c> but block <c>-0.5</c> lives in block <c>-1</c>, so a
    /// plain cast names the wrong block (and, via <see cref="Blocks.WorldBase.ChunkInWorld"/>, the wrong chunk) for
    /// every negative coordinate. Floor keeps the whole engine consistent with <c>ChunkInWorld</c>/<c>BlockInChunk</c>,
    /// which already floor. (Silk's <c>.As&lt;int&gt;()</c> rounds, which is wrong too, so these named conversions are kept.)
    /// </summary>
    public static class Vector3iExtensions
    {
        public static Vector3D<int> ToVector3i(this Vector3D<float> v) =>
            new Vector3D<int>((int)MathF.Floor(v.X), (int)MathF.Floor(v.Y), (int)MathF.Floor(v.Z));
        public static Vector3D<float> ToVector3(this Vector3D<int> v) => new Vector3D<float>(v.X, v.Y, v.Z);
    }
}

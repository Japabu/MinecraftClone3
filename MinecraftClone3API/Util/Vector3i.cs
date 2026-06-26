using Silk.NET.Maths;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Conversions between the integer block-coordinate vector (<c>Vector3i</c> = Silk.NET.Maths
    /// <c>Vector3D&lt;int&gt;</c>) and the float vector. Block coordinates need <c>(int)</c> truncation, which
    /// Silk's <c>.As&lt;int&gt;()</c> rounding doesn't give, so these two named conversions are kept.
    /// </summary>
    public static class Vector3iExtensions
    {
        public static Vector3D<int> ToVector3i(this Vector3D<float> v) => new Vector3D<int>((int)v.X, (int)v.Y, (int)v.Z);
        public static Vector3D<float> ToVector3(this Vector3D<int> v) => new Vector3D<float>(v.X, v.Y, v.Z);
    }
}

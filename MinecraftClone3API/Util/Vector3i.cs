using System;
using OpenTK.Mathematics;

// ReSharper disable InconsistentNaming

namespace MinecraftClone3API.Util
{
    public struct Vector3i : IEquatable<Vector3i>
    {
        public static Vector3i operator +(Vector3i v0, Vector3i v1)
            => new Vector3i(v0.X + v1.X, v0.Y + v1.Y, v0.Z + v1.Z);

        public static Vector3i operator -(Vector3i v0, Vector3i v1)
            => new Vector3i(v0.X - v1.X, v0.Y - v1.Y, v0.Z - v1.Z);

        public static Vector3i operator *(Vector3i v, int i) => new Vector3i(v.X * i, v.Y * i, v.Z * i);
        public static Vector3i operator *(Vector3i v1, Vector3i v2) => new Vector3i(v1.X * v2.X, v1.Y * v2.Y, v1.Z * v2.Z);

        public static bool operator ==(Vector3i v0, Vector3i v1) => v0.Equals(v1);
        public static bool operator !=(Vector3i v0, Vector3i v1) => !v0.Equals(v1);


        public static Vector3i Zero = new Vector3i(0);

        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }

        public int LengthSquared => X * X + Y * Y + Z * Z;
        public float Length => (float)Math.Sqrt(LengthSquared);

        public Vector3i(int i) : this(i, i, i)
        {
        }

        public Vector3i(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public override int GetHashCode() => ((23 + X) * 37 + Y) * 37 + Z;
        public override string ToString() => $"X:{X}, Y:{Y}, Z:{Z}";
        public override bool Equals(object obj)
        {
            var v = obj as Vector3i?;
            return v.HasValue && Equals(this, v.Value);
        }

        public bool Equals(Vector3i other) => X == other.X && Y == other.Y && Z == other.Z;
    }

    public static class Vector3iExtensions
    {
        public static Vector3i ToVector3i(this Vector3 v) => new Vector3i((int) v.X, (int) v.Y, (int) v.Z);
        public static Vector3 ToVector3(this Vector3i v) => new Vector3(v.X, v.Y, v.Z);
    }
}

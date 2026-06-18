using System;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    public struct Vector3iChunk
    {
        public ushort Binary { get; private set; }

        private Vector3iChunk(ushort value)
        {
            Binary = value;
        }

        public static Vector3iChunk FromBinary(ushort value) => new Vector3iChunk(value);

        public Vector3iChunk(int x, int y, int z)
        {
            Binary = 0;
            X = x;
            Y = y;
            Z = z;
        }

        public int X
        {
            get { return (Binary & 0x1F) >> 0; }
            set { Binary = (ushort)(Binary & ~0x1F | MathHelper.Clamp(value, 0, 31) << 0 & 0x1F); }
        }

        public int Y
        {
            get { return (Binary & 0x3E0) >> 5; }
            set { Binary = (ushort)(Binary & ~0x3E0 | MathHelper.Clamp(value, 0, 31) << 5 & 0x3E0); }
        }

        public int Z
        {
            get { return (Binary & 0x7C00) >> 10; }
            set { Binary = (ushort)(Binary & ~0x7C00 | MathHelper.Clamp(value, 0, 31) << 10 & 0x7C00); }
        }

        public int this[int id]
        {
            get
            {
                if (id == 0) return X;
                if (id == 1) return Y;
                if (id == 2) return Z;
                throw new ArgumentOutOfRangeException();
            }
            set
            {
                if (id == 0) X = value;
                else if (id == 1) Y = value;
                else if (id == 2) Z = value;
                else throw new ArgumentOutOfRangeException();
            }
        }

        public static implicit operator Vector3iChunk(Vector3i v)
        {
            return new Vector3iChunk(v.X, v.Y, v.Z);
        }
    }
}

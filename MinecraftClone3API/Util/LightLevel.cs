using System;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    public struct LightLevel
    {
        //Encodes light levels from 0-31 to a ushort BGR

        public const int Max = 31;

        public static LightLevel Zero = new LightLevel();

        public static LightLevel FromBinary(ushort value) => new LightLevel(value);
        public ushort Binary { get; private set; }

        public int Red
        {
            get { return (Binary & 0x1F) >> 0; }
            set { Binary = (ushort) (Binary & ~0x1F | MathHelper.Clamp(value, 0, 31) << 0 & 0x1F); }
        }

        public int Green
        {
            get { return (Binary & 0x3E0) >> 5; }
            set { Binary = (ushort)(Binary & ~0x3E0 | MathHelper.Clamp(value, 0, 31) << 5 & 0x3E0); }
        }

        public int Blue
        {
            get { return (Binary & 0x7C00) >> 10; }
            set { Binary = (ushort)(Binary & ~0x7C00 | MathHelper.Clamp(value, 0, 31) << 10 & 0x7C00); }
        }

        public int this[int id]
        {
            get
            {
                if (id == 0) return Red;
                if (id == 1) return Green;
                if (id == 2) return Blue;
                throw new ArgumentOutOfRangeException();
            }
            set
            {
                if (id == 0) Red = value;
                else if (id == 1) Green = value;
                else if (id == 2) Blue = value;
                else throw new ArgumentOutOfRangeException();
            }
        }

        private LightLevel(ushort value)
        {
            Binary = value;
        }

        public LightLevel(int red, int green, int blue)
        {
            Binary = 0;
            Red = red;
            Green = green;
            Blue = blue;
        }

        public Vector3 Vector3 => new Vector3(Red, Green, Blue);

        public override string ToString() => $"Red:{Red}; Green:{Green}; Blue:{Blue};";
    }
}

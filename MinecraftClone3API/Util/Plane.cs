using Silk.NET.Maths;

namespace MinecraftClone3API.Util
{
    public class Plane
    {
        public Vector3D<float> Normal;
        public float D;

        public float A => Normal.X;
        public float B => Normal.Y;
        public float C => Normal.Z;

        public Plane(Vector4D<float> v) : this(new Vector3D<float>(v.X, v.Y, v.Z), v.W)
        {
        }

        public Plane(Vector3D<float> normal, float d)
        {
            Normal = normal;
            D = d;
        }

        public void Set(Vector4D<float> v)
        {
            Normal = new Vector3D<float>(v.X, v.Y, v.Z);
            D = v.W;
        }

        public void Normalize()
        {
            var x = 1 / Normal.Length;
            Normal *= x;
            D *= x;
        }
    }
}

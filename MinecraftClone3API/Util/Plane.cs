using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    public class Plane
    {
        public Vector3 Normal;
        public float D;

        public float A => Normal.X;
        public float B => Normal.Y;
        public float C => Normal.Z;

        public Plane(Vector4 v) : this(v.Xyz, v.W)
        {
        }

        public Plane(Vector3 normal, float d)
        {
            Normal = normal;
            D = d;
        }

        public void Set(Vector4 v)
        {
            Normal = v.Xyz;
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

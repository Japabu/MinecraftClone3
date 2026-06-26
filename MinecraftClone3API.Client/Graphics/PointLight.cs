using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    public class PointLight : Light
    {
        public Vector3 Position;
        public Vector3 Color;
        public float Range;

        public PointLight(Vector3 position, Vector3 color, float range)
        {
            Position = position;
            Color = color;
            Range = range;
        }
    }
}
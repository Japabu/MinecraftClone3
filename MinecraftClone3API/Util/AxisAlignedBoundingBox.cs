using MinecraftClone3API.Blocks;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    public class AxisAlignedBoundingBox
    {
        public Vector3 Min;
        public Vector3 Max;

        public Vector3 Translation => Min + (Max - Min) * 0.5f;
        public Vector3 Scale => Max - Min;

        public AxisAlignedBoundingBox(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        public bool Intersects(AxisAlignedBoundingBox bb, out BlockFace face, out float depth)
        {
            const float delta = 1e-7f;

            face = BlockFace.Back;
            depth = 0;

            var distances = new[]
            {
                bb.Max.X - Min.X, Max.X - bb.Min.X,
                bb.Max.Y - Min.Y, Max.Y - bb.Min.Y,
                bb.Max.Z - Min.Z, Max.Z - bb.Min.Z
            };

            for (var i = 0; i < 6; i++)
            {
                if (distances[i] < delta) return false;
                if (i != 0 && !(distances[i] < depth)) continue;

                face = (BlockFace) i;
                depth = distances[i];
            }

            return true;
        }

        public AxisAlignedBoundingBox Transform(Matrix4 transform)
        {
            var scale = transform.ExtractScale();
            var translation = transform.ExtractTranslation();
            return new AxisAlignedBoundingBox(Min * scale + translation, Max * scale + translation);
        }
    }
}

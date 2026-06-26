using MinecraftClone3API.Blocks;
using Silk.NET.Maths;

namespace MinecraftClone3API.Util
{
    public class AxisAlignedBoundingBox
    {
        public Vector3D<float> Min;
        public Vector3D<float> Max;

        public Vector3D<float> Translation => Min + (Max - Min) * 0.5f;
        public Vector3D<float> Scale => Max - Min;

        public AxisAlignedBoundingBox(Vector3D<float> min, Vector3D<float> max)
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

        public AxisAlignedBoundingBox Transform(Matrix4X4<float> transform)
        {
            var scale = new Vector3D<float>(
                new Vector3D<float>(transform.Row1.X, transform.Row1.Y, transform.Row1.Z).Length,
                new Vector3D<float>(transform.Row2.X, transform.Row2.Y, transform.Row2.Z).Length,
                new Vector3D<float>(transform.Row3.X, transform.Row3.Y, transform.Row3.Z).Length);
            var translation = new Vector3D<float>(transform.Row4.X, transform.Row4.Y, transform.Row4.Z);
            return new AxisAlignedBoundingBox(Min * scale + translation, Max * scale + translation);
        }
    }
}

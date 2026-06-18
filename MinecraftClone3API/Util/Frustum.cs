using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    public class Frustum
    {
        public static Frustum FromViewProjection(Matrix4 m)
        {
            var p = new[]
            {
                new Plane(m.Column3 + m.Column0), //Left
                new Plane(m.Column3 - m.Column0), //Right

                new Plane(m.Column3 + m.Column1), //Bottom
                new Plane(m.Column3 - m.Column1), //Top

                new Plane(m.Column3 + m.Column2), //Near
                new Plane(m.Column3 - m.Column2) //Far
            };

            foreach (var plane in p)
                plane.Normalize();

            return new Frustum(p);
        }

        public readonly Plane[] Planes;

        public Frustum(Plane[] planes)
        {
            Planes = planes;
        }

        public bool SpehereIntersection(Vector3 position, float radius)
        {
            foreach (var plane in Planes)
                if (Vector3.Dot(position, plane.Normal) + plane.D + radius <= 0)
                    return false;
            return true;
        }
    }
}

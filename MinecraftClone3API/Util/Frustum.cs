using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    public class Frustum
    {
        public readonly Plane[] Planes;

        public Frustum()
        {
            Planes = new Plane[6];
            for (var i = 0; i < Planes.Length; i++)
                Planes[i] = new Plane(Vector3.Zero, 0);
        }

        /// <summary>Refills the six planes in place from a view-projection matrix so a long-lived
        /// Frustum can be re-derived each frame without allocating.</summary>
        public void Set(Matrix4 m)
        {
            Planes[0].Set(m.Column3 + m.Column0); //Left
            Planes[1].Set(m.Column3 - m.Column0); //Right
            Planes[2].Set(m.Column3 + m.Column1); //Bottom
            Planes[3].Set(m.Column3 - m.Column1); //Top
            Planes[4].Set(m.Column3 + m.Column2); //Near
            Planes[5].Set(m.Column3 - m.Column2); //Far

            foreach (var plane in Planes)
                plane.Normalize();
        }

        public static Frustum FromViewProjection(Matrix4 m)
        {
            var frustum = new Frustum();
            frustum.Set(m);
            return frustum;
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

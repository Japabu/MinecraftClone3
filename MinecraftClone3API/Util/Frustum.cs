using Silk.NET.Maths;

namespace MinecraftClone3API.Util
{
    public class Frustum
    {
        public readonly Plane[] Planes;

        public Frustum()
        {
            Planes = new Plane[6];
            for (var i = 0; i < Planes.Length; i++)
                Planes[i] = new Plane(Vector3D<float>.Zero, 0);
        }

        /// <summary>Refills the six planes in place from a view-projection matrix so a long-lived
        /// Frustum can be re-derived each frame without allocating.</summary>
        public void Set(Matrix4X4<float> m)
        {
            var col0 = new Vector4D<float>(m.Row1.X, m.Row2.X, m.Row3.X, m.Row4.X);
            var col1 = new Vector4D<float>(m.Row1.Y, m.Row2.Y, m.Row3.Y, m.Row4.Y);
            var col2 = new Vector4D<float>(m.Row1.Z, m.Row2.Z, m.Row3.Z, m.Row4.Z);
            var col3 = new Vector4D<float>(m.Row1.W, m.Row2.W, m.Row3.W, m.Row4.W);

            Planes[0].Set(col3 + col0); //Left
            Planes[1].Set(col3 - col0); //Right
            Planes[2].Set(col3 + col1); //Bottom
            Planes[3].Set(col3 - col1); //Top
            Planes[4].Set(col3 + col2); //Near
            Planes[5].Set(col3 - col2); //Far

            foreach (var plane in Planes)
                plane.Normalize();
        }

        public static Frustum FromViewProjection(Matrix4X4<float> m)
        {
            var frustum = new Frustum();
            frustum.Set(m);
            return frustum;
        }

        public bool SpehereIntersection(Vector3D<float> position, float radius)
        {
            foreach (var plane in Planes)
                if (Vector3D.Dot(position, plane.Normal) + plane.D + radius <= 0)
                    return false;
            return true;
        }
    }
}

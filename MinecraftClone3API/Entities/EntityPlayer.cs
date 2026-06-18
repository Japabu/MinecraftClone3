using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL;

namespace MinecraftClone3API.Entities
{
    public class EntityPlayer : Entity
    {
        public static readonly Matrix4 DefaultProjection =
            Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60), 16f / 9f, 0.01f, 512);

        public Frustum ViewFrustum;

        public EntityPlayer()
        {
        }

        public override void Update()
        {
            base.Update();

            var view = Matrix4.LookAt(Position, Position + Forward, Vector3.UnitY);
            ViewFrustum = Frustum.FromViewProjection(view* DefaultProjection);
        }
    }
}

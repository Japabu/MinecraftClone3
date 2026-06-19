using OpenTK.Mathematics;

namespace MinecraftClone3API.Entities
{
    public class EntityPlayer : Entity
    {
        public static readonly Matrix4 DefaultProjection =
            Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60), 16f / 9f, 0.01f, 512);

        public EntityPlayer()
        {
        }
    }
}

using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Entities
{
    public class EntityPlayer : Entity
    {
        public const float Width = 0.6f;
        public const float Height = 1.8f;
        public const float EyeHeight = 1.62f;

        public static readonly Matrix4 DefaultProjection =
            Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60), 16f / 9f, 0.01f, 512);

        public bool Flying;

        public Vector3 PrevPosition;
        public Vector3 InterpolatedPosition;

        public override Vector3 EyeOffset => new Vector3(0, EyeHeight, 0);
        public override Vector3 RenderPosition => InterpolatedPosition;

        public EntityPlayer()
        {
        }

        public AxisAlignedBoundingBox Box(Vector3 feet) => new AxisAlignedBoundingBox(
            new Vector3(feet.X - Width / 2, feet.Y, feet.Z - Width / 2),
            new Vector3(feet.X + Width / 2, feet.Y + Height, feet.Z + Width / 2));
    }
}

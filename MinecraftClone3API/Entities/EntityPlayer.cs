using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Entities
{
    public class EntityPlayer : Entity
    {
        public const float Width = 0.6f;
        public const float Height = 1.8f;
        public const float EyeHeight = 1.62f;

        /// <summary>The server-side back-reference to this player's authoritative inventory (set at login), so
        /// survival can read worn armor for damage reduction. Null on the client.</summary>
        public Inventory Inventory;

        public static readonly Matrix4 DefaultProjection =
            Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60), 16f / 9f, 0.01f, 512);

        public bool Flying;

        // Survival state. On the server these are authoritative (mutated by PlayerSurvival each tick and
        // persisted); on the client Health/Hunger/Saturation/GameMode mirror the latest PlayerStats packet
        // and the timers/LastTickPosition are unused. Defaults match a fresh Creative player.
        public float Health = PlayerSurvival.MaxHealth;
        public float Hunger = PlayerSurvival.MaxHunger;
        public float Saturation = PlayerSurvival.StartSaturation;
        public float Exhaustion;
        public int Air = PlayerSurvival.MaxAir;
        public GameMode GameMode = GameMode.Creative;

        // Server-side survival bookkeeping (cadence counters + last position for movement exhaustion).
        public int FoodTimer;
        public int DrownTimer;
        public int VoidTimer;
        public Vector3 LastTickPosition;

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

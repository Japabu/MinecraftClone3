using System;
using Silk.NET.Maths;
using MinecraftClone3API.Blocks;

namespace MinecraftClone3API.Entities
{
    public class Entity
    {
        /// <summary>Network id, assigned by the server. -1 until spawned.</summary>
        public int EntityId = -1;

        /// <summary>The registered species (null for the local/remote player, which uses the built-in path).</summary>
        public EntityType Type;

        /// <summary>Server-side back-reference used by creature/item AI (set when the entity is spawned into a
        /// <see cref="WorldServer"/>). Null on the client and for the player.</summary>
        public WorldServer ServerWorld;

        public Vector3D<float> Position;
        public Vector3D<float> Velocity;
        public Vector3D<float> Forward;
        public Vector3D<float> Right;

        public float Pitch;
        public float Yaw;

        public bool OnGround;

        /// <summary>Set by the server when the entity should be removed; drained by the world next tick.</summary>
        public bool Dead;

        /// <summary>Optional polymorphic per-entity state (the entity analog of block data) — e.g. a sheep's
        /// wool/sheared state. Null for entities that need none; server-authoritative and synced to clients.</summary>
        public EntityData Data;

        /// <summary>Ticks remaining on the damage flash. Set when the entity takes a hit and counted down each
        /// tick (server-authoritative, streamed in the entity snapshot); the client renders the model red while
        /// it is non-zero so a hit reads visually.</summary>
        public int HurtTime;

        /// <summary>The session-local id of the item this entity is holding in its main hand (0 = nothing), so the
        /// client can draw it in the hand. For remote players the server streams it in the move snapshot; the local
        /// player fills its own from the inventory at draw time. Only the player model renders a held item.</summary>
        public ushort HeldItemId;

        // Client walk-cycle animation. LimbSwing is the phase accumulator (advanced by horizontal speed) and
        // LimbSwingAmount is how strongly the limbs swing (0 standing .. ~1 walking), both driven purely from
        // the interpolated motion of incoming position updates so no extra network data is needed.
        public float LimbSwing;
        public float LimbSwingAmount;
        private float _moveSpeed;

        // Remote-entity interpolation: position updates arrive at the server tick rate (20 tps), so a
        // remote entity lerps from where it was drawn toward the new target over one tick interval instead
        // of snapping. The local player overrides RenderPosition with its own (accumulator-driven) interp.
        private Vector3D<float> _interpStart;
        private float _interpAlpha = 1f;

        public virtual Vector3D<float> EyeOffset => Vector3D<float>.Zero;
        public virtual Vector3D<float> RenderPosition => Vector3D.Lerp(_interpStart, Position, _interpAlpha);

        public Entity()
        {
            Right = new Vector3D<float>(1, 0, 0);
            Forward = new Vector3D<float>(0, 0, -1);
            Position = new Vector3D<float>(0, 0, 0);
        }

        public virtual void Update()
        {
        }

        /// <summary>Writes this entity's subclass-specific persisted state (the base transform — type, position,
        /// velocity, yaw, pitch — is handled by <see cref="EntitySerializer"/>). Default: nothing.</summary>
        internal virtual void SerializeState(System.IO.BinaryWriter writer) { }

        /// <summary>Restores the state written by <see cref="SerializeState"/>. Default: nothing.</summary>
        internal virtual void DeserializeState(System.IO.BinaryReader reader) { }

        /// <summary>Aims the interpolation at a freshly-received position: continue from where the entity is
        /// drawn right now, then lerp toward the new target over one server tick.</summary>
        public void SetInterpTarget(Vector3D<float> target, float pitch, float yaw)
        {
            _interpStart = RenderPosition;
            // Horizontal travel this tick drives the limb-swing strength (so a standing entity's legs are still).
            var dx = target.X - Position.X;
            var dz = target.Z - Position.Z;
            _moveSpeed = MathF.Sqrt(dx * dx + dz * dz) / PlayerPhysics.TickSeconds;

            Position = target;
            Pitch = pitch;
            Yaw = yaw;
            _interpAlpha = 0f;
        }

        public void UpdateInterpolation(float dt)
        {
            if (_interpAlpha < 1f)
                _interpAlpha = Math.Min(1f, _interpAlpha + dt / PlayerPhysics.TickSeconds);

            AdvanceWalkCycle(_moveSpeed, dt);
        }

        /// <summary>Advances the limb-swing walk cycle for a horizontal <paramref name="speed"/> (blocks/s),
        /// easing the swing strength toward it. Remote entities drive this from interpolated network motion;
        /// the local player drives it from its own physics (it isn't in the interpolation set). The phase
        /// speed and decay are tuned to read as a natural gait at the ~4 blocks/s walking speed.</summary>
        public void AdvanceWalkCycle(float speed, float dt)
        {
            var target = Math.Clamp(speed * 0.5f, 0f, 1f);
            LimbSwingAmount += (target - LimbSwingAmount) * Math.Min(1f, dt * 8f);
            LimbSwing += speed * dt * 2f;
        }

        public void Rotate(float pitch, float yaw)
        {
            Pitch += pitch;
            Yaw += yaw;

            Pitch = Math.Clamp(Pitch, -(MathF.PI / 2f) + 0.0001f, (MathF.PI / 2f) - 0.0001f);
            Yaw %= (MathF.PI * 2f);

            Forward = new Vector3D<float>((float)(Math.Sin(Yaw) * Math.Cos(Pitch)), (float)Math.Sin(Pitch),
                    (float)(Math.Cos(Yaw) * Math.Cos(Pitch)));
            Right = Vector3D.Normalize(Vector3D.Cross(Forward, Vector3D<float>.UnitY));
        }

        public void Move(Vector3D<float> v)
        {
            var delta = Vector3D<float>.Zero;
            delta += Right * v.X;
            delta += Vector3D<float>.UnitY * v.Y;
            delta += Vector3D.Normalize(new Vector3D<float>(Forward.X, 0, Forward.Z)) * v.Z;

            Position += delta;
        }
    }
}

using System;
using MinecraftClone3API.Blocks;
using OpenTK.Mathematics;

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

        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Forward;
        public Vector3 Right;

        public float Pitch;
        public float Yaw;

        public bool OnGround;

        /// <summary>Set by the server when the entity should be removed; drained by the world next tick.</summary>
        public bool Dead;

        // Client walk-cycle animation. LimbSwing is the phase accumulator (advanced by horizontal speed) and
        // LimbSwingAmount is how strongly the limbs swing (0 standing .. ~1 walking), both driven purely from
        // the interpolated motion of incoming position updates so no extra network data is needed.
        public float LimbSwing;
        public float LimbSwingAmount;
        private float _moveSpeed;

        // Remote-entity interpolation: position updates arrive at the server tick rate (20 tps), so a
        // remote entity lerps from where it was drawn toward the new target over one tick interval instead
        // of snapping. The local player overrides RenderPosition with its own (accumulator-driven) interp.
        private Vector3 _interpStart;
        private float _interpAlpha = 1f;

        public virtual Vector3 EyeOffset => Vector3.Zero;
        public virtual Vector3 RenderPosition => Vector3.Lerp(_interpStart, Position, _interpAlpha);

        public Entity()
        {
            Right = new Vector3(1, 0, 0);
            Forward = new Vector3(0, 0, -1);
            Position = new Vector3(0, 0, 0);
        }

        public virtual void Update()
        {
        }

        /// <summary>Aims the interpolation at a freshly-received position: continue from where the entity is
        /// drawn right now, then lerp toward the new target over one server tick.</summary>
        public void SetInterpTarget(Vector3 target, float pitch, float yaw)
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
                _interpAlpha = MathHelper.Min(1f, _interpAlpha + dt / PlayerPhysics.TickSeconds);

            // Advance the walk cycle and ease the swing strength toward the current speed. The phase speed and
            // decay are tuned to read as a natural gait at the ~4 blocks/s a wandering creature moves.
            var target = MathHelper.Clamp(_moveSpeed * 0.5f, 0f, 1f);
            LimbSwingAmount += (target - LimbSwingAmount) * MathHelper.Min(1f, dt * 8f);
            LimbSwing += _moveSpeed * dt * 2f;
        }

        public void Rotate(float pitch, float yaw)
        {
            Pitch += pitch;
            Yaw += yaw;

            Pitch = MathHelper.Clamp(Pitch, -MathHelper.PiOver2 + 0.0001f, MathHelper.PiOver2 - 0.0001f);
            Yaw %= MathHelper.TwoPi;

            Forward = new Vector3((float)(Math.Sin(Yaw) * Math.Cos(Pitch)), (float)Math.Sin(Pitch),
                    (float)(Math.Cos(Yaw) * Math.Cos(Pitch)));
            Right = Vector3.Cross(Forward, Vector3.UnitY).Normalized();
        }

        public void Move(Vector3 v)
        {
            var delta = Vector3.Zero;
            delta += Right * v.X;
            delta += Vector3.UnitY * v.Y;
            delta += new Vector3(Forward.X, 0, Forward.Z).Normalized() * v.Z;

            Position += delta;
        }
    }
}

using System;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Entities
{
    public class Entity
    {
        public Vector3 Position;
        public Vector3 Forward;
        public Vector3 Right;

        public float Pitch;
        public float Yaw;

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
            Position = target;
            Pitch = pitch;
            Yaw = yaw;
            _interpAlpha = 0f;
        }

        public void UpdateInterpolation(float dt)
        {
            if (_interpAlpha >= 1f) return;
            _interpAlpha = MathHelper.Min(1f, _interpAlpha + dt / PlayerPhysics.TickSeconds);
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

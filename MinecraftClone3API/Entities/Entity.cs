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

        public Entity()
        {
            Right = new Vector3(1, 0, 0);
            Forward = new Vector3(0, 0, -1);
            Position = new Vector3(0, 0, 0);
        }

        public virtual void Update()
        {
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

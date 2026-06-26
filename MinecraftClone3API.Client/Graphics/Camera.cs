using System;
using MinecraftClone3API.Entities;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    public class Camera
    {
        public Entity ParentEntity;

        public Vector3 Right;
        public Vector3 Forward;
        public Vector3 Position;
        public float Pitch;
        public float Yaw;

        public Matrix4 View;

        public Camera(Entity parentEntity) : this()
        {
            ParentEntity = parentEntity;
        }

        public Camera()
        {
            Right = new Vector3(1, 0, 0);
            Forward = new Vector3(0, 0, -1);
            Position = new Vector3(0, 2, 0);
        }

        public void Update()
        {
            if (ParentEntity == null)
            {
                Forward = new Vector3((float) (Math.Sin(Yaw) * Math.Cos(Pitch)), (float) Math.Sin(Pitch),
                    (float) (Math.Cos(Yaw) * Math.Cos(Pitch)));
                
                Right = View.Column0.Xyz;
            }
            else
            {
                Position = ParentEntity.RenderPosition + ParentEntity.EyeOffset;
                Forward = ParentEntity.Forward;
                Right = ParentEntity.Right;
            }

            View = Matrix4.LookAt(Position, Position + Forward, Vector3.UnitY);
        }

        /// <summary>Places the camera for a third-person view: it looks along <paramref name="lookForward"/>
        /// (the player's look vector for the over-the-shoulder view, its reverse for the front view) and is
        /// pulled back from <paramref name="eye"/> by <paramref name="distance"/> (already clamped against
        /// blocks by the caller).</summary>
        public void UpdateThirdPerson(Vector3 eye, Vector3 lookForward, float distance)
        {
            Forward = lookForward;
            Right = Vector3.Cross(Forward, Vector3.UnitY).Normalized();
            Position = eye - lookForward * distance;
            View = Matrix4.LookAt(Position, Position + Forward, Vector3.UnitY);
        }

        public void Rotate(float pitch, float yaw)
        {
            Pitch += pitch;
            Yaw += yaw;

            Pitch = MathHelper.Clamp(Pitch, -MathHelper.PiOver2 + 0.0001f, MathHelper.PiOver2 - 0.0001f);
            Yaw %= MathHelper.TwoPi;
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
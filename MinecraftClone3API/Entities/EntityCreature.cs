using System;
using MinecraftClone3API.Blocks;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Entities
{
    /// <summary>
    /// A server-driven walking creature (animal or mob). Wanders by picking a random heading every few
    /// seconds and walking it at the type's speed; a hostile type instead steers toward the nearest player
    /// within sight. Gravity + block collision come from <see cref="EntityPhysics"/>; it hops when a step
    /// blocks it. Position is server-authoritative and streamed to clients, which interpolate + animate it.
    /// </summary>
    public class EntityCreature : Entity
    {
        private const float SightRange = 16f;
        private const float JumpVelocity = 0.42f;

        private readonly Random _rng = new Random();
        private float Health;
        private int _wanderTicks;
        private float _heading;
        private bool _walking;

        public override void Update()
        {
            var world = ServerWorld;
            if (world == null) return;

            if (Health <= 0f) Health = Type.MaxHealth;

            ChooseGoal(world);

            if (_walking)
            {
                Yaw = _heading;
                var dir = new Vector3(MathF.Sin(_heading), 0, MathF.Cos(_heading));
                Velocity.X = dir.X * Type.MoveSpeed;
                Velocity.Z = dir.Z * Type.MoveSpeed;
            }

            var before = Position;
            EntityPhysics.Tick(world, this, Type.Width, Type.Height);

            // Hop over a 1-block step: a wall stopped the horizontal move (barely advanced) while grounded.
            var movedSq = (Position.X - before.X) * (Position.X - before.X) +
                          (Position.Z - before.Z) * (Position.Z - before.Z);
            if (_walking && OnGround && movedSq < Type.MoveSpeed * Type.MoveSpeed * 0.25f)
                Velocity.Y = JumpVelocity;
        }

        private void ChooseGoal(WorldServer world)
        {
            if (Type.Hostile)
            {
                var target = NearestPlayer(world);
                if (target != null && (target.Position - Position).LengthSquared < SightRange * SightRange)
                {
                    var dx = target.Position.X - Position.X;
                    var dz = target.Position.Z - Position.Z;
                    _heading = MathF.Atan2(dx, dz);
                    _walking = true;
                    return;
                }
            }

            if (--_wanderTicks > 0) return;

            // Re-roll the goal roughly every 1.5–4.5 s: usually stroll, sometimes stand still.
            _wanderTicks = 30 + _rng.Next(60);
            _walking = _rng.NextDouble() < 0.7;
            if (_walking) _heading = (float) (_rng.NextDouble() * MathHelper.TwoPi);
        }

        private EntityPlayer NearestPlayer(WorldServer world)
        {
            EntityPlayer best = null;
            var bestSq = float.MaxValue;
            lock (world.PlayerEntities)
            {
                foreach (var player in world.PlayerEntities)
                {
                    var d = (player.Position - Position).LengthSquared;
                    if (d < bestSq) { bestSq = d; best = player; }
                }
            }

            return best;
        }
    }
}

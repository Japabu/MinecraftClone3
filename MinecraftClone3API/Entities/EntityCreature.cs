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
        private const int AttackCooldownTicks = 20; // hostile melee cadence (~1 s)

        private readonly Random _rng = new Random();

        /// <summary>Current health. Server-authoritative; mutated by <see cref="EntityCombat"/>. Lazily seeded
        /// to the type's max on the first tick (the type isn't known at construction).</summary>
        public float Health;

        /// <summary>Ticks of post-hit invulnerability remaining (set by <see cref="EntityCombat"/>).</summary>
        public int HurtCooldown;

        private bool _healthInit;
        private int _attackCooldown;
        private int _wanderTicks;
        private float _heading;
        private bool _walking;

        public override void Update()
        {
            var world = ServerWorld;
            if (world == null) return;

            if (!_healthInit) { Health = Type.MaxHealth; _healthInit = true; }
            if (HurtCooldown > 0) HurtCooldown--;
            if (_attackCooldown > 0) _attackCooldown--;

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

            if (Type.Hostile && Type.AttackDamage > 0f) TryAttack(world);
        }

        // A hostile creature in melee range of a player deals contact damage on its attack cadence. The player
        // is server-authoritative for damage (its Health is broadcast) but client-authoritative for position,
        // so we gate range on the last reported position.
        private void TryAttack(WorldServer world)
        {
            if (_attackCooldown > 0) return;
            var target = NearestPlayer(world);
            if (target == null) return;

            var reach = Type.Width * 0.5f + EntityPlayer.Width * 0.5f + 0.6f;
            var dx = target.Position.X - Position.X;
            var dz = target.Position.Z - Position.Z;
            if (dx * dx + dz * dz > reach * reach) return;
            if (MathF.Abs(target.Position.Y - Position.Y) > Type.Height) return;

            PlayerSurvival.ApplyContactDamage(target, Type.AttackDamage);
            _attackCooldown = AttackCooldownTicks;
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

        internal override void SerializeState(System.IO.BinaryWriter writer)
        {
            writer.Write(Health);
            EntityData.Write(writer, Data);
        }

        internal override void DeserializeState(System.IO.BinaryReader reader)
        {
            Health = reader.ReadSingle();
            Data = EntityData.Read(reader);
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

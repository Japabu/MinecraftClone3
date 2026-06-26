using System;
using MinecraftClone3API.Blocks;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Entities
{
    /// <summary>
    /// Server-authoritative melee combat against creatures: damage application, invulnerability frames,
    /// knockback, and death (loot drop + despawn). Stateless and GL-free, mirroring
    /// <see cref="PlayerSurvival"/>; the network layer calls in when an attack request arrives, and a hostile
    /// creature's AI calls <see cref="EntityCreature"/> retaliation through <see cref="PlayerSurvival"/>.
    /// </summary>
    public static class EntityCombat
    {
        /// <summary>Damage dealt by a bare hand or a non-weapon item (Minecraft: 1 point).</summary>
        public const float BaseHandDamage = 1f;

        /// <summary>Invulnerability after a hit, so rapid clicks/contacts don't multi-hit (Minecraft: 0.5 s).</summary>
        public const int HurtCooldownTicks = 10;

        /// <summary>Applies <paramref name="damage"/> to a creature from an attacker at
        /// <paramref name="sourcePos"/>: skips it while the target is in hit-cooldown, otherwise subtracts
        /// health, knocks the target back, and on death rolls its loot table and marks it for despawn.</summary>
        public static void DamageEntity(WorldServer world, EntityCreature target, float damage, Vector3 sourcePos)
        {
            if (target == null || target.Dead || target.HurtCooldown > 0 || damage <= 0f) return;

            target.Health -= damage;
            target.HurtCooldown = HurtCooldownTicks;

            var dx = target.Position.X - sourcePos.X;
            var dz = target.Position.Z - sourcePos.Z;
            var len = MathF.Sqrt(dx * dx + dz * dz);
            if (len > 0.0001f)
            {
                target.Velocity.X += dx / len * 0.4f;
                target.Velocity.Z += dz / len * 0.4f;
            }
            target.Velocity.Y = 0.36f;

            if (target.Health <= 0f) Die(world, target);
        }

        private static void Die(WorldServer world, EntityCreature target)
        {
            var loot = target.Type?.Loot;
            if (loot != null)
            {
                var center = target.Position + new Vector3(0f, target.Type.Height * 0.5f, 0f);
                foreach (var stack in loot.Roll(world.SpawnRng))
                    world.DropItem(stack, center);
            }
            target.Dead = true;
        }
    }
}

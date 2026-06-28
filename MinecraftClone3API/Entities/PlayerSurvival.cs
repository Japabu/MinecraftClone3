using System;
using MinecraftClone3API.Blocks;

namespace MinecraftClone3API.Entities
{
    /// <summary>
    /// Server-authoritative survival simulation for a player: hunger/saturation/exhaustion drain, hunger-gated
    /// health regen, starvation, drowning, void, and block-contact (lava) damage, plus the client-reported fall-damage and the
    /// food-eating effect. Stateless (all per-player state lives on <see cref="EntityPlayer"/>); constants
    /// match Minecraft on Normal difficulty.
    /// </summary>
    public static class PlayerSurvival
    {
        public const float MaxHealth = 20f;        // 10 hearts, 1 heart = 2 points
        public const float MaxHunger = 20f;
        public const float StartSaturation = 5f;   // a fresh/respawned player's saturation
        public const int MaxAir = 300;             // ticks of air before drowning starts

        private const float RegenHungerThreshold = 18f;
        private const int FoodTickInterval = 80;   // regen / starvation cadence
        private const float ExhaustionThreshold = 4f;
        private const float RegenExhaustion = 6f;
        // Movement exhaustion per horizontal block. Movement is client-side, so the server approximates
        // activity from the reported position delta (a blend of MC's walk/sprint values) so hunger drains
        // as the player travels rather than tracking exact gait.
        private const float MoveExhaustionPerBlock = 0.04f;

        private const int DrownDamageInterval = 20; // 2 points every 20 ticks once air is gone
        private const float DrownDamage = 2f;
        private const int VoidDamageInterval = 10;
        private const float VoidDamage = 4f;
        private const int ContactDamageInterval = 10;
        // Below this Y the player is "in the void". The Overworld floor (bedrock) sits at Y -32, matching
        // Minecraft's "min build height − 64".
        private const float VoidDamageY = -96f;

        /// <summary>One 20 tps survival step for a player. Creative is immune (stats clamped full, no damage);
        /// a dead player (Health ≤ 0) is left untouched until it respawns. Death detection + the stats
        /// broadcast happen in the network layer, which reads the resulting Health.</summary>
        public static void Tick(WorldServer world, EntityPlayer p)
        {
            if (p.GameMode == GameMode.Creative)
            {
                p.Health = MaxHealth;
                p.Hunger = MaxHunger;
                p.Saturation = MaxHunger;
                p.Exhaustion = 0f;
                p.Air = MaxAir;
                p.LastTickPosition = p.Position;
                return;
            }

            if (p.Health <= 0f)
            {
                p.LastTickPosition = p.Position;
                return;
            }

            AccrueMovementExhaustion(p);
            Drown(world, p);
            Void(p);
            BlockContact(world, p);
            ApplyExhaustion(p);
            Regenerate(p);
        }

        /// <summary>Applies client-reported fall damage: <c>max(0, ceil(distance − 3))</c> points. Survival only.</summary>
        public static void ApplyFallDamage(EntityPlayer p, float fallDistance)
        {
            if (p.GameMode != GameMode.Survival || p.Health <= 0f) return;
            var damage = (int) MathF.Ceiling(fallDistance - 3f);
            if (damage > 0) p.Health -= damage;
        }

        /// <summary>Applies melee contact damage from a hostile mob (the armor-reducible damage path). Survival
        /// only; ignored when already dead. Worn armor reduces the damage (Minecraft: each defense point cuts
        /// 4%, so 20 points = −80%). Player knockback is omitted because the client owns player physics.</summary>
        public static void ApplyContactDamage(EntityPlayer p, float amount)
        {
            if (p.GameMode != GameMode.Survival || p.Health <= 0f || amount <= 0f) return;

            var defense = p.Inventory != null ? p.Inventory.ArmorDefense() : 0;
            if (defense > 0)
                amount *= 1f - MathF.Min(20, defense) / 25f;

            p.Health -= amount;
        }

        /// <summary>Eating a food item: refills hunger and adds saturation (MC: gain = nutrition · modifier · 2,
        /// capped at the new hunger level).</summary>
        public static void Eat(EntityPlayer p, float nutrition, float saturationModifier)
        {
            p.Hunger = MathF.Min(MaxHunger, p.Hunger + nutrition);
            p.Saturation = MathF.Min(p.Hunger, p.Saturation + nutrition * saturationModifier * 2f);
        }

        /// <summary>Resets a player to full stats at spawn (login default seed + respawn).</summary>
        public static void Reset(EntityPlayer p)
        {
            p.Health = MaxHealth;
            p.Hunger = MaxHunger;
            p.Saturation = StartSaturation;
            p.Exhaustion = 0f;
            p.Air = MaxAir;
            p.FoodTimer = 0;
            p.DrownTimer = 0;
            p.VoidTimer = 0;
            p.ContactDamageTimer = 0;
        }

        private static void AccrueMovementExhaustion(EntityPlayer p)
        {
            var dx = p.Position.X - p.LastTickPosition.X;
            var dz = p.Position.Z - p.LastTickPosition.Z;
            p.LastTickPosition = p.Position;

            var moved = MathF.Sqrt(dx * dx + dz * dz);
            // Ignore the teleport-sized jump on spawn/respawn so it doesn't dump a chunk of exhaustion at once.
            if (moved > 0.001f && moved < 10f) p.Exhaustion += MoveExhaustionPerBlock * moved;
        }

        private static void Drown(WorldServer world, EntityPlayer p)
        {
            var headX = (int) MathF.Floor(p.Position.X);
            var headY = (int) MathF.Floor(p.Position.Y + EntityPlayer.EyeHeight);
            var headZ = (int) MathF.Floor(p.Position.Z);

            if (world.GetBlock(headX, headY, headZ).IsLiquid)
            {
                if (p.Air > 0) p.Air--;
                else if (++p.DrownTimer >= DrownDamageInterval)
                {
                    p.DrownTimer = 0;
                    p.Health -= DrownDamage;
                }
            }
            else
            {
                p.Air = MaxAir;
                p.DrownTimer = 0;
            }
        }

        private static void Void(EntityPlayer p)
        {
            if (p.Position.Y < VoidDamageY)
            {
                if (++p.VoidTimer >= VoidDamageInterval)
                {
                    p.VoidTimer = 0;
                    p.Health -= VoidDamage;
                }
            }
            else p.VoidTimer = 0;
        }

        /// <summary>Periodic damage while the player's body overlaps a damaging block (lava, future fire). The
        /// worst <see cref="Block.ContactDamage"/> over the occupied cells is applied on the cadence; like fire
        /// in Minecraft it bypasses armor (direct health subtraction, not <see cref="ApplyContactDamage"/>).</summary>
        private static void BlockContact(WorldServer world, EntityPlayer p)
        {
            var minX = (int) MathF.Floor(p.Position.X - EntityPlayer.Width / 2);
            var maxX = (int) MathF.Floor(p.Position.X + EntityPlayer.Width / 2);
            var minY = (int) MathF.Floor(p.Position.Y);
            var maxY = (int) MathF.Floor(p.Position.Y + EntityPlayer.Height);
            var minZ = (int) MathF.Floor(p.Position.Z - EntityPlayer.Width / 2);
            var maxZ = (int) MathF.Floor(p.Position.Z + EntityPlayer.Width / 2);

            var damage = 0f;
            for (var x = minX; x <= maxX; x++)
            for (var y = minY; y <= maxY; y++)
            for (var z = minZ; z <= maxZ; z++)
                damage = MathF.Max(damage, world.GetBlock(x, y, z).ContactDamage);

            if (damage > 0f)
            {
                if (++p.ContactDamageTimer >= ContactDamageInterval)
                {
                    p.ContactDamageTimer = 0;
                    p.Health -= damage;
                }
            }
            else p.ContactDamageTimer = 0;
        }

        private static void ApplyExhaustion(EntityPlayer p)
        {
            if (p.Exhaustion <= ExhaustionThreshold) return;
            p.Exhaustion -= ExhaustionThreshold;
            if (p.Saturation > 0f) p.Saturation = MathF.Max(0f, p.Saturation - 1f);
            else p.Hunger = MathF.Max(0f, p.Hunger - 1f);
        }

        private static void Regenerate(EntityPlayer p)
        {
            if (++p.FoodTimer < FoodTickInterval) return;
            p.FoodTimer = 0;

            if (p.Hunger >= RegenHungerThreshold && p.Health < MaxHealth)
            {
                p.Health = MathF.Min(MaxHealth, p.Health + 1f);
                p.Exhaustion += RegenExhaustion;
            }
            else if (p.Hunger <= 0f && p.Health > 1f)
            {
                // Normal difficulty floors starvation at 1 health.
                p.Health -= 1f;
            }
        }
    }
}

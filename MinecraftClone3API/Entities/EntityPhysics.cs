using System;
using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Entities
{
    /// <summary>
    /// Gravity + axis-aligned block collision for non-player entities, parameterised by the entity's
    /// footprint (the player has its own tuned <see cref="PlayerPhysics"/>). Runs server-side (mobs/items are
    /// server-authoritative, unlike the client-authoritative player) once per 20 tps tick. The same
    /// per-cell <c>GetCollisionBoxes</c> sweep as the player, clipping Y then X then Z and reporting
    /// <see cref="Entity.OnGround"/> from a short downward probe.
    /// </summary>
    public static class EntityPhysics
    {
        private const float Gravity = 0.08f;
        private const float VerticalDrag = 0.98f;
        private const float GroundFriction = 0.6f;
        private const float AirFriction = 0.91f;
        private const float Epsilon = 1e-4f;
        private const float GroundProbe = 1e-3f;

        // Reused scratch for one cell's collision boxes (the server tick thread is the sole caller).
        private static readonly List<AxisAlignedBoundingBox> SolidBoxes = new List<AxisAlignedBoundingBox>(4);

        /// <summary>Applies gravity, moves <paramref name="entity"/> by its velocity with block collision, and
        /// updates its ground state. Horizontal friction is applied after the move.</summary>
        public static void Tick(WorldServer world, Entity entity, float width, float height)
        {
            var halfWidth = width / 2;

            MoveWithCollision(world, entity, halfWidth, height);

            entity.Velocity.Y = (entity.Velocity.Y - Gravity) * VerticalDrag;
            var friction = entity.OnGround ? GroundFriction : AirFriction;
            entity.Velocity.X *= friction;
            entity.Velocity.Z *= friction;
        }

        private static void MoveWithCollision(WorldServer world, Entity entity, float halfWidth, float height)
        {
            var feet = entity.Position;

            var dy = Clip(world, feet, halfWidth, height, 1, entity.Velocity.Y);
            feet.Y += dy;
            if (dy != entity.Velocity.Y) entity.Velocity.Y = 0;

            var dx = Clip(world, feet, halfWidth, height, 0, entity.Velocity.X);
            feet.X += dx;
            if (dx != entity.Velocity.X) entity.Velocity.X = 0;

            var dz = Clip(world, feet, halfWidth, height, 2, entity.Velocity.Z);
            feet.Z += dz;
            if (dz != entity.Velocity.Z) entity.Velocity.Z = 0;

            entity.Position = feet;
            entity.OnGround = Clip(world, feet, halfWidth, height, 1, -GroundProbe) != -GroundProbe;
        }

        // Clips a movement of `d` along `axis` (0=X,1=Y,2=Z) of the entity AABB at `feet` against solid blocks.
        private static float Clip(WorldServer world, Vector3 feet, float halfWidth, float height, int axis, float d)
        {
            if (d == 0) return 0;

            var min = new Vector3(feet.X - halfWidth, feet.Y, feet.Z - halfWidth);
            var max = new Vector3(feet.X + halfWidth, feet.Y + height, feet.Z + halfWidth);

            var x0 = Floor(Math.Min(min.X, min.X + (axis == 0 ? d : 0))) - 1;
            var x1 = Floor(Math.Max(max.X, max.X + (axis == 0 ? d : 0))) + 1;
            var y0 = Floor(Math.Min(min.Y, min.Y + (axis == 1 ? d : 0))) - 1;
            var y1 = Floor(Math.Max(max.Y, max.Y + (axis == 1 ? d : 0))) + 1;
            var z0 = Floor(Math.Min(min.Z, min.Z + (axis == 2 ? d : 0))) - 1;
            var z1 = Floor(Math.Max(max.Z, max.Z + (axis == 2 ? d : 0))) + 1;

            for (var x = x0; x <= x1; x++)
            for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
            {
                SolidBoxes.Clear();
                world.GetBlock(x, y, z).GetCollisionBoxes(world, new Vector3i(x, y, z), SolidBoxes);
                for (var bi = 0; bi < SolidBoxes.Count; bi++)
                {
                    var box = SolidBoxes[bi];
                    var bmin = new Vector3(x + box.Min.X, y + box.Min.Y, z + box.Min.Z);
                    var bmax = new Vector3(x + box.Max.X, y + box.Max.Y, z + box.Max.Z);
                    d = ClipAxis(min, max, bmin, bmax, axis, d);
                }
            }

            return d;
        }

        private static float ClipAxis(Vector3 min, Vector3 max, Vector3 bmin, Vector3 bmax, int axis, float d)
        {
            // The two axes orthogonal to `axis` must already overlap for the box to obstruct the move.
            if (axis != 0 && (max.X <= bmin.X + Epsilon || min.X >= bmax.X - Epsilon)) return d;
            if (axis != 1 && (max.Y <= bmin.Y + Epsilon || min.Y >= bmax.Y - Epsilon)) return d;
            if (axis != 2 && (max.Z <= bmin.Z + Epsilon || min.Z >= bmax.Z - Epsilon)) return d;

            float lo, hi, blo, bhi;
            switch (axis)
            {
                case 0: lo = min.X; hi = max.X; blo = bmin.X; bhi = bmax.X; break;
                case 1: lo = min.Y; hi = max.Y; blo = bmin.Y; bhi = bmax.Y; break;
                default: lo = min.Z; hi = max.Z; blo = bmin.Z; bhi = bmax.Z; break;
            }

            if (d > 0 && hi <= blo + Epsilon)
            {
                var gap = blo - hi;
                if (gap < d) d = gap;
            }
            else if (d < 0 && lo >= bhi - Epsilon)
            {
                var gap = bhi - lo;
                if (gap > d) d = gap;
            }

            return d;
        }

        private static int Floor(float v) => (int) Math.Floor(v);
    }
}

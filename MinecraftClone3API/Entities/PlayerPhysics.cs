using System;
using MinecraftClone3API.Blocks;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Entities
{
    public static class PlayerPhysics
    {
        public const float TickSeconds = 0.05f;

        private const float Gravity = 0.08f;
        private const float VerticalDrag = 0.98f;
        private const float JumpVelocity = 0.42f;
        private const float GroundFriction = 0.6f * 0.91f;
        private const float AirFriction = 0.91f;
        private const float GroundAccel = 0.1f;
        private const float AirAccel = 0.02f;
        private const float SprintMultiplier = 1.3f;

        private const float HalfWidth = EntityPlayer.Width / 2;
        private const float Height = EntityPlayer.Height;
        private const float Epsilon = 1e-4f;
        private const float GroundProbe = 1e-3f;

        public static void Tick(WorldBase world, EntityPlayer p, Vector2 wishDir, bool jump, bool sprint)
        {
            var friction = p.OnGround ? GroundFriction : AirFriction;
            var accel = (p.OnGround ? GroundAccel : AirAccel) * (sprint ? SprintMultiplier : 1f);

            p.Velocity.X += wishDir.X * accel;
            p.Velocity.Z += wishDir.Y * accel;

            if (jump && p.OnGround) p.Velocity.Y = JumpVelocity;

            // Minecraft order: move with the current velocity (so a jump's first tick rises the full
            // 0.42), THEN apply gravity + friction for the next tick. Applying gravity before the move
            // ate the jump impulse and capped the apex at ~0.83 blocks — too low to reach a 1-block step.
            MoveWithCollision(world, p);

            p.Velocity.Y = (p.Velocity.Y - Gravity) * VerticalDrag;
            p.Velocity.X *= friction;
            p.Velocity.Z *= friction;
        }

        private static void MoveWithCollision(WorldBase world, EntityPlayer p)
        {
            var feet = p.Position;

            var min = new Vector3(feet.X - HalfWidth, feet.Y, feet.Z - HalfWidth);
            var max = new Vector3(feet.X + HalfWidth, feet.Y + Height, feet.Z + HalfWidth);
            var dy = ClipY(world, min, max, p.Velocity.Y);
            feet.Y += dy;
            if (dy != p.Velocity.Y) p.Velocity.Y = 0;

            min = new Vector3(feet.X - HalfWidth, feet.Y, feet.Z - HalfWidth);
            max = new Vector3(feet.X + HalfWidth, feet.Y + Height, feet.Z + HalfWidth);
            var dx = ClipX(world, min, max, p.Velocity.X);
            feet.X += dx;
            if (dx != p.Velocity.X) p.Velocity.X = 0;

            min = new Vector3(feet.X - HalfWidth, feet.Y, feet.Z - HalfWidth);
            max = new Vector3(feet.X + HalfWidth, feet.Y + Height, feet.Z + HalfWidth);
            var dz = ClipZ(world, min, max, p.Velocity.Z);
            feet.Z += dz;
            if (dz != p.Velocity.Z) p.Velocity.Z = 0;

            p.Position = feet;

            // Ground state from an explicit downward probe, not the Y-clip outcome: a tick that enters
            // with Velocity.Y==0 (spawn, just un-flew) or lands exactly flush would otherwise read
            // airborne for one tick (no jump, wrong friction).
            min = new Vector3(feet.X - HalfWidth, feet.Y, feet.Z - HalfWidth);
            max = new Vector3(feet.X + HalfWidth, feet.Y + Height, feet.Z + HalfWidth);
            p.OnGround = ClipY(world, min, max, -GroundProbe) != -GroundProbe;
        }

        private static float ClipY(WorldBase world, Vector3 min, Vector3 max, float dy)
        {
            if (dy == 0) return 0;

            var x0 = Floor(min.X) - 1;
            var x1 = Floor(max.X) + 1;
            var z0 = Floor(min.Z) - 1;
            var z1 = Floor(max.Z) + 1;
            int y0, y1;
            if (dy < 0) { y0 = Floor(min.Y + dy) - 1; y1 = Floor(min.Y) + 1; }
            else { y0 = Floor(max.Y) - 1; y1 = Floor(max.Y + dy) + 1; }

            for (var x = x0; x <= x1; x++)
            for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
            {
                if (!TrySolidBox(world, x, y, z, out var bmin, out var bmax)) continue;
                if (max.X <= bmin.X + Epsilon || min.X >= bmax.X - Epsilon) continue;
                if (max.Z <= bmin.Z + Epsilon || min.Z >= bmax.Z - Epsilon) continue;

                if (dy > 0 && max.Y <= bmin.Y + Epsilon)
                {
                    var d = bmin.Y - max.Y;
                    if (d < dy) dy = d;
                }
                else if (dy < 0 && min.Y >= bmax.Y - Epsilon)
                {
                    var d = bmax.Y - min.Y;
                    if (d > dy) dy = d;
                }
            }

            return dy;
        }

        private static float ClipX(WorldBase world, Vector3 min, Vector3 max, float dx)
        {
            if (dx == 0) return 0;

            var y0 = Floor(min.Y) - 1;
            var y1 = Floor(max.Y) + 1;
            var z0 = Floor(min.Z) - 1;
            var z1 = Floor(max.Z) + 1;
            int x0, x1;
            if (dx < 0) { x0 = Floor(min.X + dx) - 1; x1 = Floor(min.X) + 1; }
            else { x0 = Floor(max.X) - 1; x1 = Floor(max.X + dx) + 1; }

            for (var x = x0; x <= x1; x++)
            for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
            {
                if (!TrySolidBox(world, x, y, z, out var bmin, out var bmax)) continue;
                if (max.Y <= bmin.Y + Epsilon || min.Y >= bmax.Y - Epsilon) continue;
                if (max.Z <= bmin.Z + Epsilon || min.Z >= bmax.Z - Epsilon) continue;

                if (dx > 0 && max.X <= bmin.X + Epsilon)
                {
                    var d = bmin.X - max.X;
                    if (d < dx) dx = d;
                }
                else if (dx < 0 && min.X >= bmax.X - Epsilon)
                {
                    var d = bmax.X - min.X;
                    if (d > dx) dx = d;
                }
            }

            return dx;
        }

        private static float ClipZ(WorldBase world, Vector3 min, Vector3 max, float dz)
        {
            if (dz == 0) return 0;

            var x0 = Floor(min.X) - 1;
            var x1 = Floor(max.X) + 1;
            var y0 = Floor(min.Y) - 1;
            var y1 = Floor(max.Y) + 1;
            int z0, z1;
            if (dz < 0) { z0 = Floor(min.Z + dz) - 1; z1 = Floor(min.Z) + 1; }
            else { z0 = Floor(max.Z) - 1; z1 = Floor(max.Z + dz) + 1; }

            for (var x = x0; x <= x1; x++)
            for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
            {
                if (!TrySolidBox(world, x, y, z, out var bmin, out var bmax)) continue;
                if (max.X <= bmin.X + Epsilon || min.X >= bmax.X - Epsilon) continue;
                if (max.Y <= bmin.Y + Epsilon || min.Y >= bmax.Y - Epsilon) continue;

                if (dz > 0 && max.Z <= bmin.Z + Epsilon)
                {
                    var d = bmin.Z - max.Z;
                    if (d < dz) dz = d;
                }
                else if (dz < 0 && min.Z >= bmax.Z - Epsilon)
                {
                    var d = bmax.Z - min.Z;
                    if (d > dz) dz = d;
                }
            }

            return dz;
        }

        private static bool TrySolidBox(WorldBase world, int x, int y, int z, out Vector3 min, out Vector3 max)
        {
            min = default;
            max = default;

            var pos = new Vector3i(x, y, z);
            var block = world.GetBlock(x, y, z);
            if (block.CanPassThrough(world, pos)) return false;

            var bb = block.GetBoundingBox(world, pos);
            if (bb == null) return false;

            var center = new Vector3(x, y, z);
            min = center + bb.Min;
            max = center + bb.Max;
            return true;
        }

        private static int Floor(float v) => (int) Math.Floor(v);
    }
}

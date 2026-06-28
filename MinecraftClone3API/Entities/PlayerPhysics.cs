using System;
using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

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

        private const float WaterAccel = 0.02f;
        private const float WaterSprintAccel = 0.04f;
        private const float WaterDrag = 0.8f;
        private const float WaterGravity = 0.02f;
        private const float SwimImpulse = 0.04f;

        // On a ladder: horizontal motion is clamped so the player clings, the fall is slowed, and pressing a
        // movement key (or jump) climbs up — Minecraft's gentle ladder feel.
        private const float LadderClingSpeed = 0.15f;
        private const float LadderClimbSpeed = 0.12f;
        private const float LadderDescendSpeed = 0.15f;

        private const float HalfWidth = EntityPlayer.Width / 2;
        private const float Height = EntityPlayer.Height;
        private const float Epsilon = 1e-4f;
        private const float GroundProbe = 1e-3f;
        private const float StepHeight = 0.6f;

        /// <summary>One walk/swim physics step. Returns true if a horizontal axis was blocked by terrain this
        /// tick (the player ran into a wall) — the caller uses it to drop sprint, as Minecraft does.</summary>
        public static bool Tick(WorldBase world, EntityPlayer p, Vector2D<float> wishDir, bool jump, bool sprint)
        {
            if (IsInLiquid(world, p))
                return TickInWater(world, p, wishDir, jump, sprint);

            var friction = p.OnGround ? GroundFriction : AirFriction;
            var accel = (p.OnGround ? GroundAccel : AirAccel) * (sprint ? SprintMultiplier : 1f);

            p.Velocity.X += wishDir.X * accel;
            p.Velocity.Z += wishDir.Y * accel;

            if (jump && p.OnGround) p.Velocity.Y = JumpVelocity;

            // On a ladder, override the vertical motion (and rein in the horizontal) just before the move, so the
            // player clings and climbs instead of falling. Gravity still applies after the move and is re-clamped
            // here next tick.
            if (IsOnLadder(world, p)) ApplyLadder(p, wishDir, jump);

            // Minecraft order: move with the current velocity (so a jump's first tick rises the full
            // 0.42), THEN apply gravity + friction for the next tick. Applying gravity before the move
            // ate the jump impulse and capped the apex at ~0.83 blocks — too low to reach a 1-block step.
            var collided = MoveWithCollision(world, p);

            p.Velocity.Y = (p.Velocity.Y - Gravity) * VerticalDrag;
            p.Velocity.X *= friction;
            p.Velocity.Z *= friction;
            return collided;
        }

        /// <summary>The "80%" swim model: gentle water accel (faster while sprint-swimming), Space buoys up,
        /// otherwise sink slowly; all velocity damped by <see cref="WaterDrag"/>. Liquid never collides (it's
        /// pass-through), so the swept-collision and ground probe still run via <see cref="MoveWithCollision"/>.</summary>
        private static bool TickInWater(WorldBase world, EntityPlayer p, Vector2D<float> wishDir, bool jump, bool sprint)
        {
            var accel = sprint ? WaterSprintAccel : WaterAccel;
            p.Velocity.X += wishDir.X * accel;
            p.Velocity.Z += wishDir.Y * accel;

            if (jump) p.Velocity.Y += SwimImpulse;

            var collided = MoveWithCollision(world, p);

            p.Velocity.X *= WaterDrag;
            p.Velocity.Z *= WaterDrag;
            p.Velocity.Y = p.Velocity.Y * WaterDrag - WaterGravity;
            return collided;
        }

        private static bool IsInLiquid(WorldBase world, EntityPlayer p)
        {
            var pos = p.Position;
            return IsLiquidAt(world, pos.X, pos.Y + 0.1f, pos.Z)
                || IsLiquidAt(world, pos.X, pos.Y + Height * 0.5f, pos.Z);
        }

        private static bool IsLiquidAt(WorldBase world, float x, float y, float z)
            => world.GetBlock(BlockCoord(x), BlockCoord(y), BlockCoord(z)).IsLiquid;

        private static int BlockCoord(float v) => (int) Math.Floor(v);

        // True if any cell the player's AABB overlaps is a climbable block (a ladder). Cheap: a handful of cells.
        private static bool IsOnLadder(WorldBase world, EntityPlayer p)
        {
            var pos = p.Position;
            var x0 = Floor(pos.X - HalfWidth); var x1 = Floor(pos.X + HalfWidth);
            var z0 = Floor(pos.Z - HalfWidth); var z1 = Floor(pos.Z + HalfWidth);
            var y0 = Floor(pos.Y); var y1 = Floor(pos.Y + Height);
            for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
            for (var z = z0; z <= z1; z++)
            {
                var cell = new Vector3D<int>(x, y, z);
                if (world.GetBlock(x, y, z).IsClimbable(world, cell)) return true;
            }
            return false;
        }

        private static void ApplyLadder(EntityPlayer p, Vector2D<float> wishDir, bool jump)
        {
            p.Velocity.X = Math.Clamp(p.Velocity.X, -LadderClingSpeed, LadderClingSpeed);
            p.Velocity.Z = Math.Clamp(p.Velocity.Z, -LadderClingSpeed, LadderClingSpeed);

            // Pressing a movement key (into the ladder) or jump climbs; otherwise slide down slowly.
            var climbing = jump || wishDir.X != 0f || wishDir.Y != 0f;
            p.Velocity.Y = climbing ? LadderClimbSpeed : Math.Max(p.Velocity.Y, -LadderDescendSpeed);
        }

        private static bool MoveWithCollision(WorldBase world, EntityPlayer p)
        {
            var velX = p.Velocity.X;
            var velY = p.Velocity.Y;
            var velZ = p.Velocity.Z;
            var grounded = p.OnGround;

            var feet = p.Position;
            var dy = ClipYFrom(world, feet, velY);
            feet.Y += dy;
            if (dy != velY) p.Velocity.Y = 0;

            // Horizontal collide (X then Z) from the post-vertical position.
            var afterY = feet;
            var cdx = ClipXFrom(world, afterY, velX);
            var cdz = ClipZFrom(world, new Vector3D<float>(afterY.X + cdx, afterY.Y, afterY.Z), velZ);
            var blockedX = cdx != velX;
            var blockedZ = cdz != velZ;

            feet = new Vector3D<float>(afterY.X + cdx, afterY.Y, afterY.Z + cdz);
            var resVelX = blockedX ? 0f : velX;
            var resVelZ = blockedZ ? 0f : velZ;

            // Auto-step: when grounded, NOT rising, and a horizontal axis was blocked, retry the full
            // horizontal move raised by StepHeight (up → horizontal → drop back down) and keep it if it
            // advanced farther. StepHeight 0.6 = Minecraft: climbs slabs/partial blocks. The velY <= 0 gate
            // is essential — without it the step fires on the jump tick (velY = +0.42), stacking StepHeight
            // on top of the jump's rise and clipping the player straight up a full block. Stepping only while
            // settling onto the ground means the jump arc alone (apex ~1.25) decides if a 1-block is cleared.
            if (grounded && velY <= 0f && (blockedX || blockedZ))
            {
                var up = ClipYFrom(world, afterY, StepHeight);
                var stepped = new Vector3D<float>(afterY.X, afterY.Y + up, afterY.Z);
                var sdx = ClipXFrom(world, stepped, velX);
                var sdz = ClipZFrom(world, new Vector3D<float>(stepped.X + sdx, stepped.Y, stepped.Z), velZ);
                stepped = new Vector3D<float>(stepped.X + sdx, stepped.Y, stepped.Z + sdz);
                stepped.Y += ClipYFrom(world, stepped, -up);

                if (sdx * sdx + sdz * sdz > cdx * cdx + cdz * cdz)
                {
                    feet = stepped;
                    resVelX = sdx != velX ? 0f : velX;
                    resVelZ = sdz != velZ ? 0f : velZ;
                }
            }

            p.Velocity.X = resVelX;
            p.Velocity.Z = resVelZ;
            p.Position = feet;

            // Ground state from an explicit downward probe, not the Y-clip outcome: a tick that enters
            // with Velocity.Y==0 (spawn, just un-flew) or lands exactly flush would otherwise read
            // airborne for one tick (no jump, wrong friction).
            p.OnGround = ClipYFrom(world, feet, -GroundProbe) != -GroundProbe;

            // A wall hit: a non-zero horizontal wish was clipped to zero on either axis (after auto-step).
            return (resVelX == 0f && velX != 0f) || (resVelZ == 0f && velZ != 0f);
        }

        private static float ClipYFrom(WorldBase world, Vector3D<float> feet, float dy)
        {
            var min = new Vector3D<float>(feet.X - HalfWidth, feet.Y, feet.Z - HalfWidth);
            var max = new Vector3D<float>(feet.X + HalfWidth, feet.Y + Height, feet.Z + HalfWidth);
            return ClipY(world, min, max, dy);
        }

        private static float ClipXFrom(WorldBase world, Vector3D<float> feet, float dx)
        {
            var min = new Vector3D<float>(feet.X - HalfWidth, feet.Y, feet.Z - HalfWidth);
            var max = new Vector3D<float>(feet.X + HalfWidth, feet.Y + Height, feet.Z + HalfWidth);
            return ClipX(world, min, max, dx);
        }

        private static float ClipZFrom(WorldBase world, Vector3D<float> feet, float dz)
        {
            var min = new Vector3D<float>(feet.X - HalfWidth, feet.Y, feet.Z - HalfWidth);
            var max = new Vector3D<float>(feet.X + HalfWidth, feet.Y + Height, feet.Z + HalfWidth);
            return ClipZ(world, min, max, dz);
        }

        private static float ClipY(WorldBase world, Vector3D<float> min, Vector3D<float> max, float dy)
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
                var n = GetSolidBoxes(world, x, y, z);
                for (var bi = 0; bi < n; bi++)
                {
                    var box = _solidBoxes[bi];
                    var bmin = new Vector3D<float>(x + box.Min.X, y + box.Min.Y, z + box.Min.Z);
                    var bmax = new Vector3D<float>(x + box.Max.X, y + box.Max.Y, z + box.Max.Z);
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
            }

            return dy;
        }

        private static float ClipX(WorldBase world, Vector3D<float> min, Vector3D<float> max, float dx)
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
                var n = GetSolidBoxes(world, x, y, z);
                for (var bi = 0; bi < n; bi++)
                {
                    var box = _solidBoxes[bi];
                    var bmin = new Vector3D<float>(x + box.Min.X, y + box.Min.Y, z + box.Min.Z);
                    var bmax = new Vector3D<float>(x + box.Max.X, y + box.Max.Y, z + box.Max.Z);
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
            }

            return dx;
        }

        private static float ClipZ(WorldBase world, Vector3D<float> min, Vector3D<float> max, float dz)
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
                var n = GetSolidBoxes(world, x, y, z);
                for (var bi = 0; bi < n; bi++)
                {
                    var box = _solidBoxes[bi];
                    var bmin = new Vector3D<float>(x + box.Min.X, y + box.Min.Y, z + box.Min.Z);
                    var bmax = new Vector3D<float>(x + box.Max.X, y + box.Max.Y, z + box.Max.Z);
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
            }

            return dz;
        }

        // Reused scratch for the block's collision boxes at one cell (block-local, centred). Single writer
        // (the player physics tick runs on the client main thread), refilled per cell, consumed immediately.
        private static readonly List<AxisAlignedBoundingBox> _solidBoxes = new List<AxisAlignedBoundingBox>(4);

        // Fills _solidBoxes with the cell's collision boxes (block-local). Returns the count; 0 = pass-through.
        private static int GetSolidBoxes(WorldBase world, int x, int y, int z)
        {
            _solidBoxes.Clear();
            world.GetBlock(x, y, z).GetCollisionBoxes(world, new Vector3D<int>(x, y, z), _solidBoxes);
            return _solidBoxes.Count;
        }

        private static int Floor(float v) => (int) Math.Floor(v);
    }
}

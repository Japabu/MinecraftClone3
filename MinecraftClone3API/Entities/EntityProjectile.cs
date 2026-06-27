using System;
using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.Entities
{
    /// <summary>
    /// A thrown ender pearl. Server-authoritative: flies under light gravity until its path enters a solid
    /// block, then queues a teleport of the player who threw it and despawns. The motion is sub-stepped so a
    /// fast pearl can't tunnel through a thin wall. Like Minecraft, the teleport target is the pearl's impact
    /// point (its last position before entering the block); unlike vanilla — which leaves you clipped into a
    /// wall on a dead-on side hit (the MC-2164 suffocation bug) — if that point would embed the player it's
    /// nudged to the nearest clear spot. Position is streamed to clients, which only interpolate + render it (a
    /// billboard sprite); the teleport itself is dispatched by the network layer from
    /// <see cref="WorldServer.PendingTeleports"/>.
    /// </summary>
    public class EntityProjectile : Entity
    {
        private const float Gravity = 0.03f;
        private const float Drag = 0.99f;
        private const float StepLength = 0.2f;     // sub-step size for the swept collision check (blocks)
        private const int MaxLifeTicks = 200;      // ~10 s safety despawn if it never hits anything

        private const float PlayerHalfWidth = EntityPlayer.Width / 2f;
        private const float NudgeStep = 0.25f;     // search granularity when pushing the player out of a wall
        private const int MaxNudge = 16;           // … up to this many steps (4 blocks) before giving up

        /// <summary>EntityId of the player who threw it — the teleport recipient. Server-only.</summary>
        public int OwnerId;

        private int _life;

        private static readonly List<AxisAlignedBoundingBox> Scratch = new List<AxisAlignedBoundingBox>(4);

        public override void Update()
        {
            var world = ServerWorld;
            if (world == null) return;

            Velocity.Y -= Gravity;
            Velocity *= Drag;
            Yaw = MathF.Atan2(Velocity.X, Velocity.Z);

            var move = Velocity;
            var steps = Math.Max(1, (int) MathF.Ceiling(move.Length / StepLength));
            var step = move / steps;
            for (var i = 0; i < steps; i++)
            {
                var next = Position + step;
                if (IsSolid(world, next))
                {
                    Land(world);
                    return;
                }
                Position = next;
            }

            if (++_life > MaxLifeTicks) Land(world);
        }

        // Fallback push directions, tried after the reverse-of-travel direction: up first (climb out onto the
        // surface), then the four horizontals as a last resort.
        private static readonly Vector3D<float>[] FallbackDirs =
            {Vector3D<float>.UnitY, -Vector3D<float>.UnitX, Vector3D<float>.UnitX, -Vector3D<float>.UnitZ, Vector3D<float>.UnitZ};

        private void Land(WorldServer world)
        {
            // Only teleport if we can resolve a player-clear destination; otherwise the pearl just fizzles, so a
            // throw can never strand the player inside geometry.
            if (TryResolveLanding(world, out var target))
                world.PendingTeleports.Enqueue(new WorldServer.Teleport(OwnerId, target));
            Dead = true;
        }

        /// <summary>The teleport destination: the pearl's impact point if a player-sized box already fits there
        /// (the vanilla target — normal for landing on a top face), else the player is pushed <b>back along the
        /// pearl's incoming path</b> (the side it flew in from is guaranteed open, so it never ends up on the far
        /// side of a thick wall), with up/horizontal fallbacks. Returns false if nothing clear is within reach.</summary>
        private bool TryResolveLanding(WorldServer world, out Vector3D<float> result)
        {
            if (PlayerBoxClear(world, Position)) { result = Position; return true; }

            if (Velocity.LengthSquared > 1e-6f && PushClear(world, -Vector3D.Normalize(Velocity), out result))
                return true;

            foreach (var dir in FallbackDirs)
                if (PushClear(world, dir, out result))
                    return true;

            result = Position;
            return false;
        }

        private bool PushClear(WorldServer world, Vector3D<float> dir, out Vector3D<float> result)
        {
            for (var i = 1; i <= MaxNudge; i++)
            {
                result = Position + dir * (i * NudgeStep);
                if (PlayerBoxClear(world, result)) return true;
            }
            result = Position;
            return false;
        }

        // True if a player-sized box stood at `feet` overlaps no solid collision box (cells are local 0..1, so
        // each box is offset by its cell origin as EntityPhysics does).
        private static bool PlayerBoxClear(WorldServer world, Vector3D<float> feet)
        {
            var min = new Vector3D<float>(feet.X - PlayerHalfWidth, feet.Y, feet.Z - PlayerHalfWidth);
            var max = new Vector3D<float>(feet.X + PlayerHalfWidth, feet.Y + EntityPlayer.Height, feet.Z + PlayerHalfWidth);

            for (var x = Floor(min.X); x <= Floor(max.X); x++)
            for (var y = Floor(min.Y); y <= Floor(max.Y); y++)
            for (var z = Floor(min.Z); z <= Floor(max.Z); z++)
            {
                Scratch.Clear();
                world.GetBlock(x, y, z).GetCollisionBoxes(world, new Vector3D<int>(x, y, z), Scratch);
                for (var i = 0; i < Scratch.Count; i++)
                {
                    var b = Scratch[i];
                    if (min.X < x + b.Max.X && max.X > x + b.Min.X &&
                        min.Y < y + b.Max.Y && max.Y > y + b.Min.Y &&
                        min.Z < z + b.Max.Z && max.Z > z + b.Min.Z)
                        return false;
                }
            }
            return true;
        }

        private static bool IsSolid(WorldServer world, Vector3D<float> pos)
        {
            var bp = new Vector3D<int>((int) MathF.Floor(pos.X), (int) MathF.Floor(pos.Y), (int) MathF.Floor(pos.Z));
            Scratch.Clear();
            world.GetBlock(bp.X, bp.Y, bp.Z).GetCollisionBoxes(world, bp, Scratch);
            // GetCollisionBoxes reports boxes in local cell space (0..1), so offset by the cell origin (as
            // EntityPhysics does). Only count a hit when the pearl point is actually inside one, so it passes
            // over the empty half of a slab/stair instead of stopping at the cell boundary.
            for (var i = 0; i < Scratch.Count; i++)
            {
                var b = Scratch[i];
                if (pos.X >= bp.X + b.Min.X && pos.X <= bp.X + b.Max.X &&
                    pos.Y >= bp.Y + b.Min.Y && pos.Y <= bp.Y + b.Max.Y &&
                    pos.Z >= bp.Z + b.Min.Z && pos.Z <= bp.Z + b.Max.Z)
                    return true;
            }
            return false;
        }

        private static int Floor(float v) => (int) MathF.Floor(v);
    }
}

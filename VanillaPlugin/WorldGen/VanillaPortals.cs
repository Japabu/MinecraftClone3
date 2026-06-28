using System;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using MinecraftClone3API.WorldGen;
using Silk.NET.Maths;
using VanillaPlugin.BlockDatas;
using VanillaPlugin.Blocks;

namespace VanillaPlugin.WorldGen
{
    /// <summary>
    /// Vanilla obsidian-portal rules: lighting a frame with flint &amp; steel, the Overworld↔Nether link with
    /// 8:1 horizontal scaling, and finding-or-building the destination portal. Implements the engine's
    /// <see cref="IDimensionPortals"/> so the engine's dimension-travel can drive it without knowing about
    /// obsidian or the Nether.
    /// </summary>
    public class VanillaPortals : IDimensionPortals
    {
        private const int MaxSize = 21;        // max interior width/height of a portal
        private const int CoordScale = 8;      // Overworld:Nether horizontal block ratio
        private const int SearchRadius = 16;   // horizontal reach when looking for an existing destination portal
        private const int SearchHeight = 48;   // vertical reach: the return Y is clamped/floor-adjusted, so the
                                               // original portal can sit well above/below the scaled Y

        private static readonly Vector3D<int> Up = new Vector3D<int>(0, 1, 0);

        private Block _obsidian;
        private Block _portal;

        private Block Obsidian => _obsidian ??= GameRegistry.GetBlock("Vanilla:Obsidian");
        private Block Portal => _portal ??= GameRegistry.GetBlock(BlockNetherPortal.Key);

        // ---- flint & steel ignition -------------------------------------------------------------

        /// <summary>Tries to light an obsidian frame whose interior contains <paramref name="cell"/> (the air
        /// block the player clicked toward). Detects a vertical rectangle along either horizontal axis and
        /// fills it with portal blocks. Returns true if a valid frame was lit.</summary>
        public bool TryLight(WorldServer world, Vector3D<int> cell)
        {
            if (!IsEmpty(world, cell)) return false;
            return TryAxis(world, cell, new Vector3D<int>(1, 0, 0)) || TryAxis(world, cell, new Vector3D<int>(0, 0, 1));
        }

        private bool TryAxis(WorldServer world, Vector3D<int> start, Vector3D<int> axis)
        {
            // Drop to the bottom interior row (the cell whose neighbour below is the obsidian sill).
            var bottom = start;
            for (var i = 0; i < MaxSize && IsEmpty(world, bottom - Up); i++) bottom -= Up;
            if (!IsObsidian(world, bottom - Up)) return false;

            // Horizontal extent of the bottom row, bounded by obsidian jambs.
            var left = bottom;
            for (var i = 0; i < MaxSize && IsEmpty(world, left - axis); i++) left -= axis;
            if (!IsObsidian(world, left - axis)) return false;

            var right = bottom;
            for (var i = 0; i < MaxSize && IsEmpty(world, right + axis); i++) right += axis;
            if (!IsObsidian(world, right + axis)) return false;

            var width = Dot(right - left, axis) + 1;
            if (width < 2 || width > MaxSize) return false;

            // Walk up while the whole interior row is empty and both jambs are obsidian.
            var height = 0;
            for (var h = 0; h < MaxSize; h++)
            {
                var rowLeft = left + Up * h;
                if (!IsObsidian(world, rowLeft - axis)) return false;
                if (!IsObsidian(world, right + Up * h + axis)) return false;

                var rowEmpty = true;
                for (var i = 0; i < width; i++)
                    if (!IsEmpty(world, rowLeft + axis * i)) { rowEmpty = false; break; }
                if (!rowEmpty) break;
                height++;
            }
            if (height < 3 || height > MaxSize) return false;

            // Obsidian lintel and sill across the full width.
            for (var i = 0; i < width; i++)
            {
                if (!IsObsidian(world, left + axis * i - Up)) return false;
                if (!IsObsidian(world, left + Up * height + axis * i)) return false;
            }

            var axisMeta = axis.X != 0 ? BlockNetherPortal.AxisX : BlockNetherPortal.AxisZ;
            for (var h = 0; h < height; h++)
            for (var i = 0; i < width; i++)
                PlacePortal(world, left + Up * h + axis * i, axisMeta);
            return true;
        }

        // ---- IDimensionPortals ------------------------------------------------------------------

        public bool IsPortalBlock(Block block) => block == Portal;

        public Vector3D<int> SearchExtent => new Vector3D<int>(SearchRadius, SearchHeight, SearchRadius);

        public string TargetDimension(string fromDimensionKey)
        {
            if (fromDimensionKey == OverworldDimension.Key) return NetherDimension.Key;
            if (fromDimensionKey == NetherDimension.Key) return OverworldDimension.Key;
            return null;
        }

        public Vector3D<int> ScaleToTarget(string fromKey, string toKey, Vector3D<int> fromBlock)
        {
            if (fromKey == OverworldDimension.Key && toKey == NetherDimension.Key)
                return new Vector3D<int>(FloorDiv(fromBlock.X, CoordScale),
                    Math.Clamp(fromBlock.Y, NetherChunkGenerator.LavaLevel + 4, NetherChunkGenerator.CeilingY - 8),
                    FloorDiv(fromBlock.Z, CoordScale));

            if (fromKey == NetherDimension.Key && toKey == OverworldDimension.Key)
                return new Vector3D<int>(fromBlock.X * CoordScale, Math.Clamp(fromBlock.Y, 8, 120),
                    fromBlock.Z * CoordScale);

            return fromBlock;
        }

        public Vector3D<float> EnsureDestinationPortal(WorldServer world, Vector3D<int> approx)
        {
            if (TryFindExisting(world, approx, out var existing)) return existing;
            return Build(world, AdjustToFloor(world, approx));
        }

        /// <summary>Scans the destination column for a standable floor (solid block with two open cells above)
        /// near <paramref name="approx"/> so the portal lands on the Overworld surface / a Nether cavern floor
        /// rather than mid-air or buried. Falls back to <paramref name="approx"/> (Build clears a pocket anyway).</summary>
        private Vector3D<int> AdjustToFloor(WorldServer world, Vector3D<int> approx)
        {
            for (var y = approx.Y + 24; y >= approx.Y - 48 && y > 2; y--)
            {
                if (!IsSolid(world, new Vector3D<int>(approx.X, y, approx.Z))) continue;
                if (IsSolid(world, new Vector3D<int>(approx.X, y + 1, approx.Z))) continue;
                if (IsSolid(world, new Vector3D<int>(approx.X, y + 2, approx.Z))) continue;
                return new Vector3D<int>(approx.X, y + 1, approx.Z);
            }
            return approx;
        }

        private static bool IsSolid(WorldServer world, Vector3D<int> p)
        {
            var b = world.GetBlock(p.X, p.Y, p.Z);
            return b.Id != 0 && !b.CanPassThrough(world, p);
        }

        /// <summary>Scans a box around <paramref name="approx"/> (within loaded chunks) for an existing portal
        /// block; returns the feet position centred in it. So returning through a portal lands at the same one.</summary>
        private bool TryFindExisting(WorldServer world, Vector3D<int> approx, out Vector3D<float> stand)
        {
            stand = Vector3D<float>.Zero;
            var best = int.MaxValue;
            var found = false;
            var hit = new Vector3D<int>();
            for (var dx = -SearchRadius; dx <= SearchRadius; dx++)
            for (var dz = -SearchRadius; dz <= SearchRadius; dz++)
            for (var dy = -SearchHeight; dy <= SearchHeight; dy++)
            {
                var p = new Vector3D<int>(approx.X + dx, approx.Y + dy, approx.Z + dz);
                if (world.GetBlock(p.X, p.Y, p.Z) != Portal) continue;
                // Nearest by horizontal distance — the scaled X/Z is the real anchor, so the closest column wins
                // even when its portal sits well above/below the clamped approx.Y.
                var d = dx * dx + dz * dz;
                if (d >= best) continue;
                best = d;
                hit = p;
                found = true;
            }

            if (!found) return false;
            // Drop to the bottom of this portal column so the player stands on the sill.
            var feet = hit;
            while (world.GetBlock(feet.X, feet.Y - 1, feet.Z) == Portal) feet -= Up;
            stand = new Vector3D<float>(feet.X + 0.5f, feet.Y, feet.Z + 0.5f);
            return true;
        }

        /// <summary>Carves a pocket and builds a standard 4×5 obsidian frame (2×3 portal interior) along the X
        /// axis at <paramref name="at"/>, lighting it. Returns the feet position in the portal mouth.</summary>
        private Vector3D<float> Build(WorldServer world, Vector3D<int> at)
        {
            var x0 = at.X;
            var y0 = at.Y;
            var z0 = at.Z;

            // Obsidian standing platform under and around the frame footprint.
            for (var dx = -1; dx <= 2; dx++)
            for (var dz = -1; dz <= 1; dz++)
                world.SetBlock(new Vector3D<int>(x0 + dx, y0 - 1, z0 + dz), Obsidian);

            // Clear the airspace around the frame so the player isn't suffocated/burned.
            for (var dx = -1; dx <= 2; dx++)
            for (var dy = 0; dy <= 4; dy++)
            for (var dz = -1; dz <= 1; dz++)
                world.SetBlock(new Vector3D<int>(x0 + dx, y0 + dy, z0 + dz), BlockRegistry.BlockAir);

            // Frame: jambs at x0-1 and x0+2, sill at y0-1, lintel at y0+3, across x0-1..x0+2.
            for (var dy = 0; dy <= 2; dy++)
            {
                world.SetBlock(new Vector3D<int>(x0 - 1, y0 + dy, z0), Obsidian);
                world.SetBlock(new Vector3D<int>(x0 + 2, y0 + dy, z0), Obsidian);
            }
            for (var dx = -1; dx <= 2; dx++)
            {
                world.SetBlock(new Vector3D<int>(x0 + dx, y0 - 1, z0), Obsidian);
                world.SetBlock(new Vector3D<int>(x0 + dx, y0 + 3, z0), Obsidian);
            }

            // Portal interior (2 wide × 3 tall), pane along the X axis.
            for (var dx = 0; dx <= 1; dx++)
            for (var dy = 0; dy <= 2; dy++)
                PlacePortal(world, new Vector3D<int>(x0 + dx, y0 + dy, z0), BlockNetherPortal.AxisX);

            return new Vector3D<float>(x0 + 1.0f, y0, z0 + 0.5f);
        }

        // ---- helpers ----------------------------------------------------------------------------

        /// <summary>Places a portal block carrying its axis (so it renders the correct oriented thin pane).</summary>
        private void PlacePortal(WorldServer world, Vector3D<int> p, int axisMeta)
        {
            world.SetBlock(p, Portal);
            world.SetBlockData(p.X, p.Y, p.Z, new BlockDataMetadata(axisMeta));
        }

        private bool IsObsidian(WorldServer world, Vector3D<int> p) => world.GetBlock(p.X, p.Y, p.Z) == Obsidian;

        private bool IsEmpty(WorldServer world, Vector3D<int> p)
        {
            var b = world.GetBlock(p.X, p.Y, p.Z);
            return b == BlockRegistry.BlockAir || b == Portal;
        }

        private static int Dot(Vector3D<int> a, Vector3D<int> b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static int FloorDiv(int a, int b)
        {
            var q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }
    }
}

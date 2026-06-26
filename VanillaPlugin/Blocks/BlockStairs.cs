using System;
using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using VanillaPlugin.BlockDatas;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// Straight stairs (no corner variants). The geometry + textures are the real Minecraft
    /// <c>block/oak_stairs</c> model loaded from the resource pack; orientation lives in the block metadata
    /// because the engine parses no blockstate files. The metadata int packs facing in bits 0-1 and the
    /// top/bottom half in bit 2; <see cref="GetModelTransform"/> rotates the model to match and
    /// <see cref="GetCollisionBoxes"/> builds the matching L-shaped collision (so the player walks up them).
    /// The vanilla base model's tall step is on +X (east); the facing angles rotate that to the stored facing.
    /// </summary>
    public class BlockStairs : Block
    {
        private const int North = 0; // -Z
        private const int South = 1; // +Z
        private const int East = 2;  // +X (the model's base orientation)
        private const int West = 3;  // -X

        public BlockStairs() : base("OakStairs")
        {
            MinecraftId = "minecraft:oak_stairs";
            Model = ResourceReader.ReadBlockModel("minecraft:block/oak_stairs");
        }

        public override bool IsFullBlock(WorldBase world, Vector3i blockPos) => false;

        public override int GetPlacementMetadata(EntityPlayer player, BlockRaytraceResult ray)
        {
            // Facing = the player's horizontal look direction (the tall step rises toward where you look).
            // Forward = (sin yaw, _, cos yaw): yaw 0 looks +Z (south). Flip this mapping if placement feels
            // reversed. Half: clicking the underside of a block makes an upside-down (top) stair, as in MC.
            var fx = Math.Sin(player.Yaw);
            var fz = Math.Cos(player.Yaw);
            int facing;
            if (Math.Abs(fz) >= Math.Abs(fx)) facing = fz >= 0 ? South : North;
            else facing = fx >= 0 ? East : West;

            var top = ray != null && ray.Face == BlockFace.Bottom ? 1 : 0;
            return (facing & 0x3) | (top << 2);
        }

        public override void OnPlaced(WorldBase world, Vector3i blockPos, EntityPlayer player, int metadata)
            => world.SetBlockData(blockPos, new BlockDataMetadata(metadata));

        public override Matrix4 GetModelTransform(WorldBase world, Vector3i blockPos)
        {
            Decode(world, blockPos, out var facing, out var top);
            var rot = Matrix4.CreateRotationY(FacingAngle(facing));
            // Top half = the vanilla "upside-down" stair: flip about X (y -> -y) before rotating to facing.
            if (top) rot = Matrix4.CreateRotationX(MathHelper.Pi) * rot;
            return rot;
        }

        public override void GetCollisionBoxes(WorldBase world, Vector3i blockPos, List<AxisAlignedBoundingBox> boxes)
        {
            Decode(world, blockPos, out var facing, out var top);

            // The full-footprint slab fills one Y half; the tall step fills the other Y half on the facing side.
            var slabMinY = top ? 0f : -0.5f;
            var slabMaxY = top ? 0.5f : 0f;
            boxes.Add(new AxisAlignedBoundingBox(new Vector3(-0.5f, slabMinY, -0.5f), new Vector3(0.5f, slabMaxY, 0.5f)));

            var stepMinY = top ? -0.5f : 0f;
            var stepMaxY = top ? 0f : 0.5f;
            float sxMin = -0.5f, sxMax = 0.5f, szMin = -0.5f, szMax = 0.5f;
            switch (facing)
            {
                case East: sxMin = 0f; break;
                case West: sxMax = 0f; break;
                case South: szMin = 0f; break;
                default: szMax = 0f; break; // North
            }
            boxes.Add(new AxisAlignedBoundingBox(new Vector3(sxMin, stepMinY, szMin), new Vector3(sxMax, stepMaxY, szMax)));
        }

        // Rotation taking the base model's +X (east) tall step to the stored facing (engine axes: +X east,
        // +Z south). Derived from the Y-rotation math, so the rendered step and the collision step agree.
        private static float FacingAngle(int facing)
        {
            switch (facing)
            {
                case North: return MathHelper.PiOver2;
                case South: return -MathHelper.PiOver2;
                case West: return MathHelper.Pi;
                default: return 0f; // East
            }
        }

        private static void Decode(WorldBase world, Vector3i pos, out int facing, out bool top)
        {
            var meta = (world.GetBlockData(pos) as BlockDataMetadata)?.Metadata ?? 0;
            facing = meta & 0x3;
            top = (meta & 0x4) != 0;
        }
    }
}

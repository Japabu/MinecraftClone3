using System;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;
using VanillaPlugin.BlockDatas;

namespace VanillaPlugin.Blocks
{
    /// <summary>
    /// A chest: a directional 27-slot storage container, server-authoritative. It renders as a block entity (a
    /// separate chest box model drawn by the client's block-entity renderer, not baked into the chunk mesh), so
    /// it declares <see cref="RendersAsBlockEntity"/> and the geo/texture paths instead of a block model. Its
    /// contents live in <see cref="BlockDataChest"/>; right-clicking opens the chest screen (client-side only)
    /// and breaking it drops every stored stack.
    /// </summary>
    public class BlockChest : Block
    {
        // facing index 0..3 = north/east/south/west -> the Y rotation (radians) the chest model is drawn at, so
        // the front (with the latch) points in the stored facing. Tunable against a visual check.
        private static readonly float[] FacingYaw =
            { MathF.PI, MathF.PI / 2f, 0f, -MathF.PI / 2f };

        public BlockChest() : base("Chest")
        {
            MinecraftId = "minecraft:chest";
        }

        public override bool RendersAsBlockEntity => true;
        public override string BlockEntityModelPath => "Vanilla/Models/Entity/chest.geo.json";
        public override string BlockEntityTexturePath => "minecraft:entity/chest/normal";

        // Not a full opaque cube: neighbours must keep the faces they'd otherwise cull against it, and light
        // passes around the model.
        public override bool IsFullBlock(WorldBase world, Vector3D<int> blockPos) => false;

        public override float Hardness => 2.5f;
        public override ToolType PreferredTool => ToolType.Axe;

        public override float GetBlockEntityRotation(WorldBase world, Vector3D<int> blockPos)
            => FacingYaw[(Data(world, blockPos)?.Facing ?? 2) & 0x3];

        public override int GetPlacementMetadata(EntityPlayer player, BlockRaytraceResult ray)
        {
            // The front faces the placing player, exactly like the furnace.
            var fx = Math.Sin(player.Yaw);
            var fz = Math.Cos(player.Yaw);
            int look;
            if (Math.Abs(fz) >= Math.Abs(fx)) look = fz >= 0 ? 2 : 0; // south : north
            else look = fx >= 0 ? 1 : 3;                              // east : west
            return (look + 2) % 4;
        }

        public override void OnPlaced(WorldBase world, Vector3D<int> blockPos, EntityPlayer player, int metadata)
            => world.SetBlockData(blockPos, new BlockDataChest((byte) (metadata & 0x3)));

        public override void OnBroken(WorldServer world, Vector3D<int> blockPos)
        {
            var data = Data(world, blockPos);
            if (data == null) return;
            var centre = blockPos.ToVector3() + new Vector3D<float>(0.5f, 0.5f, 0.5f);
            foreach (var slot in data.Slots)
                if (!slot.IsEmpty) world.DropItem(slot, centre);
        }

        public override bool OnActivated(WorldBase world, Vector3D<int> blockPos, EntityPlayer player)
        {
            if (!(world is WorldClient client)) return false;
            StateEngine.AddOverlay(new GuiChest(client, blockPos));
            return true;
        }

        private static BlockDataChest Data(WorldBase world, Vector3D<int> blockPos)
            => world.GetBlockData(blockPos) as BlockDataChest;
    }
}

using System;
using System.Collections.Generic;
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
    /// A furnace: a directional block that smelts an input item using fuel over time, server-authoritative. The
    /// facing + lit appearance comes from the pack's <c>blockstates/furnace.json</c> (selected by
    /// <see cref="GetBlockState"/>); the per-tick smelting runs in <see cref="OnServerTick"/> against the block's
    /// <see cref="BlockDataFurnace"/>, and right-clicking opens the furnace screen (client-side only). While
    /// burning it emits light and renders the lit model.
    /// </summary>
    public class BlockFurnace : Block
    {
        // facing index -> Minecraft blockstate value; index 0..3 = north/east/south/west.
        private static readonly string[] FacingNames = { "north", "east", "south", "west" };

        public BlockFurnace() : base("Furnace")
        {
            MinecraftId = "minecraft:furnace";
            ModelPath = "minecraft:block/furnace";
            BlockStateId = "minecraft:furnace";
        }

        protected override CreativeTab DefaultCreativeTab => CreativeTab.FunctionalBlocks;

        public override bool NeedsServerTick => true;

        public override LightLevel GetLightLevel(WorldBase world, Vector3D<int> blockPos) =>
            Data(world, blockPos)?.IsBurning == true ? new LightLevel(13, 11, 8) : LightLevel.Zero;

        // East: with no stored facing (the item-icon render, IconWorld) point the front (the model's north
        // face) at +X, which the icon camera shows on the right — so the icon shows the furnace front, as
        // vanilla does, instead of a plain side. A placed furnace always has its own facing in block data.
        private const byte IconFacing = 1;

        public override IReadOnlyDictionary<string, string> GetBlockState(WorldBase world, Vector3D<int> blockPos)
        {
            var data = Data(world, blockPos);
            var facing = data != null ? data.Facing : IconFacing;
            return new Dictionary<string, string>
            {
                { "facing", FacingNames[facing & 0x3] },
                { "lit", data?.IsBurning == true ? "true" : "false" }
            };
        }

        public override int GetPlacementMetadata(EntityPlayer player, BlockRaytraceResult ray)
        {
            // The front faces the placing player: take the player's horizontal look direction and use its
            // opposite (north<->south, east<->west), exactly like vanilla's horizontal-facing blocks.
            var fx = Math.Sin(player.Yaw);
            var fz = Math.Cos(player.Yaw);
            int look;
            if (Math.Abs(fz) >= Math.Abs(fx)) look = fz >= 0 ? 2 : 0; // south : north
            else look = fx >= 0 ? 1 : 3;                              // east : west
            return (look + 2) % 4;
        }

        public override void OnPlaced(WorldBase world, Vector3D<int> blockPos, EntityPlayer player, int metadata)
            => world.SetBlockData(blockPos, new BlockDataFurnace((byte) (metadata & 0x3)));

        public override bool OnActivated(WorldBase world, Vector3D<int> blockPos, EntityPlayer player)
        {
            if (!(world is WorldClient client)) return false;
            StateEngine.AddOverlay(new GuiFurnace(client, blockPos));
            return true;
        }

        public override void OnServerTick(WorldServer world, Vector3D<int> blockPos)
        {
            var data = Data(world, blockPos);
            if (data == null) return;

            var wasBurning = data.IsBurning;
            var changed = false;

            if (data.IsBurning) data.BurnTime--;

            var recipe = GameRegistry.MatchSmelting(data.Slots[BlockDataFurnace.SlotInput]);
            var canSmelt = CanSmelt(data, recipe);

            if (!data.IsBurning && canSmelt)
            {
                var fuel = data.Slots[BlockDataFurnace.SlotFuel];
                var ticks = FurnaceFuel.GetBurnTicks(fuel.ItemId);
                if (ticks > 0)
                {
                    data.BurnTime = data.BurnTimeTotal = ticks;
                    data.SetSlot(BlockDataFurnace.SlotFuel, Decrement(fuel));
                    changed = true;
                }
            }

            if (data.IsBurning && canSmelt)
            {
                data.CookTimeTotal = recipe.CookingTime;
                data.CookTime++;
                if (data.CookTime >= data.CookTimeTotal)
                {
                    data.CookTime = 0;
                    Smelt(data, recipe);
                    changed = true;
                }
            }
            else if (data.CookTime > 0)
            {
                data.CookTime = Math.Max(0, data.CookTime - 2);
            }

            // A lit/unlit transition changes the model + light, so go through SetBlockData (remesh + relight +
            // save). Otherwise just flag the chunk for saving — progress is streamed to viewers separately.
            if (wasBurning != data.IsBurning) world.SetBlockData(blockPos, data);
            else if (changed || wasBurning) world.TouchBlockDataForSave(blockPos);
        }

        private static bool CanSmelt(BlockDataFurnace data, SmeltingRecipe recipe)
        {
            if (recipe == null) return false;
            var output = data.Slots[BlockDataFurnace.SlotOutput];
            if (output.IsEmpty) return true;
            if (!output.SameItem(recipe.Result)) return false;
            var max = output.Item?.MaxStackSize ?? ItemStack.MaxStackSize;
            return output.Count + recipe.Result.Count <= max;
        }

        private static void Smelt(BlockDataFurnace data, SmeltingRecipe recipe)
        {
            data.SetSlot(BlockDataFurnace.SlotInput, Decrement(data.Slots[BlockDataFurnace.SlotInput]));

            var output = data.Slots[BlockDataFurnace.SlotOutput];
            data.SetSlot(BlockDataFurnace.SlotOutput, output.IsEmpty
                ? recipe.Result
                : output.WithCount(output.Count + recipe.Result.Count));
        }

        private static ItemStack Decrement(ItemStack stack)
            => stack.Count <= 1 ? ItemStack.Empty : stack.WithCount(stack.Count - 1);

        private static BlockDataFurnace Data(WorldBase world, Vector3D<int> blockPos)
            => world.GetBlockData(blockPos) as BlockDataFurnace;
    }
}

using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    /// <summary>A crafting table: right-clicking it opens the 3×3 crafting screen instead of placing the held
    /// item. The interaction runs client-side only (the headless server never calls <see cref="OnActivated"/>).</summary>
    public class BlockCraftingTable : BlockBasic
    {
        public BlockCraftingTable() : base("CraftingTable", "minecraft:block/crafting_table", true)
        {
        }

        protected override CreativeTab DefaultCreativeTab => CreativeTab.FunctionalBlocks;

        public override bool OnActivated(WorldBase world, Vector3D<int> blockPos, EntityPlayer player)
        {
            if (!(world is WorldClient client)) return false;
            StateEngine.AddOverlay(new GuiCraftingTable(client));
            return true;
        }
    }
}

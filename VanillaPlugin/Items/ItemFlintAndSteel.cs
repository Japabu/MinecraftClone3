using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using OpenTK.Mathematics;
using VanillaPlugin.WorldGen;

namespace VanillaPlugin.Items
{
    /// <summary>Flint &amp; steel: right-clicking the inside of an obsidian frame lights a Nether portal (the
    /// ignition is server-authoritative, like a spawn egg). The clicked-toward cell is the air block where the
    /// fire would land, which must be a cell inside the frame.</summary>
    public class ItemFlintAndSteel : Item
    {
        private readonly VanillaPortals _portals;

        public ItemFlintAndSteel(VanillaPortals portals) : base("FlintAndSteel")
        {
            _portals = portals;
        }

        public override string MinecraftId => "minecraft:flint_and_steel";
        public override string TexturePath => "minecraft/textures/item/flint_and_steel.png";
        public override int MaxStackSize => 1;
        public override bool IsUsable => true;

        public override void OnUseServer(WorldServer world, EntityPlayer player, Vector3 position)
        {
            var cell = new Vector3i(
                (int) System.MathF.Floor(position.X),
                (int) System.MathF.Floor(position.Y),
                (int) System.MathF.Floor(position.Z));
            _portals.TryLight(world, cell);
        }
    }
}

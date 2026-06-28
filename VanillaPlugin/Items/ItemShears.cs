using System;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;
using VanillaPlugin.Entities;

namespace VanillaPlugin.Items
{
    /// <summary>Shears: right-clicking a woolly sheep shears it — hides its wool overlay (via
    /// <see cref="SheepData"/>) and drops 1–3 wool. Server-authoritative; the wool item is resolved by registry
    /// key at use time.</summary>
    public class ItemShears : Item
    {
        private const string WoolKey = "Vanilla:WhiteWool";
        private readonly Random _rng = new Random();

        public ItemShears() : base("Shears")
        {
        }

        public override string TexturePath => "minecraft/textures/item/shears.png";
        public override string MinecraftId => "minecraft:shears";
        public override int MaxStackSize => 1;
        public override bool UsableOnEntity => true;

        public override void OnUseOnEntity(WorldServer world, EntityPlayer player, Entity target)
        {
            if (!(target.Data is SheepData sheep) || sheep.Sheared) return;
            sheep.Sheared = true;
            if (GameRegistry.TryGetItem(WoolKey, out var wool))
                world.DropItem(new ItemStack(wool.Id, 1 + _rng.Next(3)),
                    target.Position + new Vector3D<float>(0f, target.Type.Height * 0.5f, 0f));
        }
    }
}

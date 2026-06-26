using System;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using OpenTK.Mathematics;

namespace VanillaPlugin.Items
{
    /// <summary>An ender pearl: right-clicking throws a projectile along the player's look direction that, on
    /// hitting a block, teleports the thrower to the impact point (Minecraft's signature movement item). The
    /// throw is server-authoritative — the projectile entity carries the thrower's id and queues the teleport
    /// when it lands. Stacks to 16 and is consumed on use; it can be thrown aiming at the sky.</summary>
    public class ItemEnderPearl : Item
    {
        private const float ThrowSpeed = 1.1f;     // initial blocks/tick along the look direction

        private readonly EntityType _projectileType;

        public ItemEnderPearl(EntityType projectileType) : base("EnderPearl")
        {
            _projectileType = projectileType;
        }

        public override int MaxStackSize => 16;
        public override string TexturePath => "minecraft/textures/item/ender_pearl.png";
        public override string MinecraftId => "minecraft:ender_pearl";
        public override bool IsUsable => true;
        public override bool RequiresBlockTarget => false;
        public override bool ConsumesOnUse => true;

        public override void OnUseServer(WorldServer world, EntityPlayer player, Vector3 position)
        {
            var forward = new Vector3(
                (float) (Math.Sin(player.Yaw) * Math.Cos(player.Pitch)),
                (float) Math.Sin(player.Pitch),
                (float) (Math.Cos(player.Yaw) * Math.Cos(player.Pitch)));

            var spawn = player.Position + new Vector3(0f, EntityPlayer.EyeHeight, 0f) + forward * 0.3f;
            var pearl = (EntityProjectile) world.SpawnEntity(_projectileType, spawn);
            pearl.OwnerId = player.EntityId;
            pearl.Velocity = forward * ThrowSpeed + new Vector3(0f, 0.1f, 0f);
        }
    }
}

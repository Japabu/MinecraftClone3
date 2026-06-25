using MinecraftClone3API.Items;

namespace MinecraftClone3API.Entities
{
    /// <summary>
    /// A dropped item stack. Server-side it falls under gravity, settles on the ground, and despawns after a
    /// while; <see cref="CanPickup"/> gates a short delay after spawning so a just-broken block isn't instantly
    /// re-collected. The network layer scans for items near players and transfers them into the inventory.
    /// Clients render it as the spinning, bobbing 3D icon of the stack's block.
    /// </summary>
    public class EntityItem : Entity
    {
        public const float Width = 0.25f;
        public const float Height = 0.25f;

        private const int LifetimeTicks = 20 * 300; // ~5 minutes

        public ItemStack Stack;
        public int Age;

        /// <summary>Ticks after spawn before this drop can be collected. Default ~0.5 s for block-break drops;
        /// player-thrown drops set it higher so they don't fly straight back into the thrower.</summary>
        public int PickupDelay = 10;

        public bool CanPickup => Age >= PickupDelay;

        public override void Update()
        {
            var world = ServerWorld;
            if (world == null) return;

            EntityPhysics.Tick(world, this, Width, Height);

            Age++;
            if (Age >= LifetimeTicks) Dead = true;
        }
    }
}

using System;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// One clickable cell in a <see cref="ContainerScreen"/>: a GUI-space rectangle plus get/set accessors onto
    /// wherever the stack actually lives (a crafting grid cell, a player-inventory slot mirrored to the server,
    /// a crafting result, or a creative infinite source). The container does all the cursor logic against these
    /// accessors, so a screen just declares its slots and where each one reads/writes.
    /// </summary>
    public sealed class Slot
    {
        public Rectangle Bounds;
        public Func<ItemStack> Get;
        public Action<ItemStack> Set;

        /// <summary>A crafting result: nothing can be placed into it; taking from it consumes the recipe via
        /// <see cref="OnTakeOutput"/>.</summary>
        public bool IsOutput;

        /// <summary>An infinite supply (the creative item list): left-click picks a full stack, right-click one;
        /// placing a held stack onto it discards the held stack.</summary>
        public bool IsSource;

        /// <summary>Called once per crafted batch taken from an output slot, to consume the ingredients.</summary>
        public Action OnTakeOutput;

        /// <summary>Region id used to route shift-click quick-move (e.g. grid / main inventory / hotbar). The
        /// meaning is the owning screen's; <see cref="ContainerScreen.SlotsInGroup"/> filters by it.</summary>
        public int Group;

        /// <summary>Optional gate restricting what may be placed here (e.g. a helmet slot only accepts helmets);
        /// null accepts anything. Checked by <see cref="ContainerScreen"/> on every placement path.</summary>
        public Func<ItemStack, bool> CanAccept;

        public Slot(Rectangle bounds, Func<ItemStack> get, Action<ItemStack> set)
        {
            Bounds = bounds;
            Get = get;
            Set = set;
        }

        public bool Contains(Vector2D<float> p) =>
            p.X >= Bounds.MinX && p.X < Bounds.MaxX && p.Y >= Bounds.MinY && p.Y < Bounds.MaxY;
    }
}

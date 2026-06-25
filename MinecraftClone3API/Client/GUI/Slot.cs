using System;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

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

        public Slot(Rectangle bounds, Func<ItemStack> get, Action<ItemStack> set)
        {
            Bounds = bounds;
            Get = get;
            Set = set;
        }

        public bool Contains(Vector2 p) =>
            p.X >= Bounds.MinX && p.X < Bounds.MaxX && p.Y >= Bounds.MinY && p.Y < Bounds.MaxY;
    }
}

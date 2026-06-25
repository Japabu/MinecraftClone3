using System.Collections.Generic;

namespace MinecraftClone3API.Items
{
    /// <summary>
    /// How long (in server ticks) each item burns as furnace fuel. Vanilla keeps these values in code (there is
    /// no fuel data file in the resource pack), so the burn-time table is defined here and resolved against the
    /// items we actually registered when recipes load (see <c>RecipeLoader.BuildFuel</c>). An item not in the
    /// table is not fuel.
    /// </summary>
    public static class FurnaceFuel
    {
        private static readonly Dictionary<ushort, int> Ticks = new Dictionary<ushort, int>();

        /// <summary>The burn time in ticks of one of the given item as fuel, or 0 if it is not fuel.</summary>
        public static int GetBurnTicks(ushort itemId) => Ticks.TryGetValue(itemId, out var t) ? t : 0;

        public static bool IsFuel(ushort itemId) => GetBurnTicks(itemId) > 0;

        internal static void Reset() => Ticks.Clear();

        internal static void Set(ushort itemId, int ticks)
        {
            if (ticks > 0) Ticks[itemId] = ticks;
        }
    }
}

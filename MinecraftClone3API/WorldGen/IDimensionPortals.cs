using MinecraftClone3API.Blocks;
using OpenTK.Mathematics;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// Content-provided portal rules the engine's dimension-travel uses. The engine knows nothing about
    /// obsidian, the Nether, or 8:1 scaling — a plugin registers one of these (see
    /// <c>PluginContext.RegisterPortals</c>) to define which block is a portal, which dimension a portal in
    /// dimension X leads to, how coordinates map between them, and how to find-or-build the destination portal.
    /// With none registered, standing in any block never transfers dimensions.
    /// </summary>
    public interface IDimensionPortals
    {
        /// <summary>True if <paramref name="block"/> is a portal surface a standing player transfers through.</summary>
        bool IsPortalBlock(Block block);

        /// <summary>The dimension key a portal in <paramref name="fromDimensionKey"/> leads to, or null if a
        /// portal there links nowhere.</summary>
        string TargetDimension(string fromDimensionKey);

        /// <summary>Maps a portal block position in the source dimension to the approximate destination block in
        /// the target dimension (the per-dimension coordinate scale, e.g. 8:1 for Overworld↔Nether).</summary>
        Vector3i ScaleToTarget(string fromKey, string toKey, Vector3i fromBlock);

        /// <summary>Server-side: ensure a usable portal exists in <paramref name="world"/> near
        /// <paramref name="approx"/> — reusing a nearby existing one or building a fresh obsidian frame — and
        /// return the feet position where the arriving player should stand. Called once the destination column
        /// has streamed in, so it may read and write blocks freely.</summary>
        Vector3 EnsureDestinationPortal(WorldServer world, Vector3i approx);
    }
}

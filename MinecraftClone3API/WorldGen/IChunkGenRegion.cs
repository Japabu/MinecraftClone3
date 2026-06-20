using MinecraftClone3API.Blocks;

namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// The read/write surface a <see cref="Feature"/> sees during decoration. Reads expose the generator's
    /// pure (noise) heightmap and biome map at any world column, plus the blocks of the <em>chunk being
    /// generated</em>; writes are <b>clipped to that chunk</b> — a <see cref="SetBlock"/> outside its bounds
    /// is silently dropped. A feature therefore runs identically for every chunk it overlaps (its origin
    /// chunk and the neighbours) and each chunk keeps only the part that falls inside it, so a tree or vein
    /// crossing a border is consistent with no neighbour writes. Coordinates are absolute world coordinates.
    /// </summary>
    public interface IChunkGenRegion
    {
        int SeaLevel { get; }

        int SurfaceHeight(int worldX, int worldZ);

        Biome BiomeAt(int worldX, int worldZ);

        Block GetBlock(int worldX, int worldY, int worldZ);

        void SetBlock(int worldX, int worldY, int worldZ, Block block);
    }
}

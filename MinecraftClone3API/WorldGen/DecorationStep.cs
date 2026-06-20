namespace MinecraftClone3API.WorldGen
{
    /// <summary>
    /// Ordered phases of post-terrain decoration. Features are placed step-by-step in this enum order
    /// (ores before vegetation), so later steps see the blocks earlier ones wrote. New steps slot in by
    /// value; the generator iterates the declared order.
    /// </summary>
    public enum DecorationStep
    {
        Ores,
        Vegetation
    }
}

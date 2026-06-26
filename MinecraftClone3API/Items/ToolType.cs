namespace MinecraftClone3API.Items
{
    /// <summary>The tool category a block prefers and a tool item provides. Mining is faster when a held
    /// tool's <see cref="Item.ToolType"/> matches the block's <see cref="MinecraftClone3API.Blocks.Block.PreferredTool"/>
    /// (Minecraft's <c>mineable/*</c> tags).</summary>
    public enum ToolType : byte
    {
        None = 0,
        Pickaxe,
        Axe,
        Shovel,
        Sword
    }
}

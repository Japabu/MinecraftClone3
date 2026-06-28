namespace MinecraftClone3API.Items
{
    /// <summary>
    /// The creative-menu category an item (or a block, via its <see cref="ItemBlock"/>) belongs to, mirroring
    /// Minecraft's <c>CreativeModeTabs</c>. The creative inventory groups every registered item into these tabs.
    /// The Search and Survival-Inventory tabs are special (not item categories) and are handled by the screen
    /// itself, so they have no entry here.
    /// </summary>
    public enum CreativeTab
    {
        BuildingBlocks,
        ColoredBlocks,
        NaturalBlocks,
        FunctionalBlocks,
        Redstone,
        ToolsAndUtilities,
        Combat,
        FoodAndDrink,
        Ingredients,
        SpawnEggs
    }
}

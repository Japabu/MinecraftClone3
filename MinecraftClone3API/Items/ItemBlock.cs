using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Items
{
    /// <summary>
    /// The auto-generated item form of a block: placing it places the block, and its inventory icon is the
    /// block's 3D isometric render. Registered automatically alongside every block (see
    /// <c>PluginContext.Register(Block)</c>) under the same registry key, so every block is an item.
    /// </summary>
    public class ItemBlock : Item
    {
        public readonly Block Block;

        public ItemBlock(Block block) : base(block.Name) => Block = block;

        public override string MinecraftId => Block.MinecraftId;

        public override Block GetBlock() => Block;

        protected override CreativeTab DefaultCreativeTab => Block.CreativeTab;

        public override string GetUnlocalizedName() =>
            Block.MinecraftId != null
                ? Identifier.TranslationKey("block", Block.MinecraftId)
                : I18N.UnlocalizedName(Block.RegistryKey, "blocks");
    }
}

using MinecraftClone3API.Items;
using MinecraftClone3API.Util;

namespace VanillaPlugin.Items
{
    /// <summary>A plain non-placeable item (stick, ingot, gem, food, …) rendered from a 2D resource-pack
    /// sprite. The texture path is a full in-jar asset path, loaded lazily client-side; the Minecraft content
    /// id (for translations and recipe matching) is derived from it.</summary>
    public class ItemSimple : Item
    {
        private readonly string _texturePath;
        private readonly string _minecraftId;

        public ItemSimple(string name, string texturePath) : base(name)
        {
            _texturePath = texturePath;
            _minecraftId = Identifier.FromResourcePath(texturePath);
        }

        public override string TexturePath => _texturePath;

        public override string MinecraftId => _minecraftId;
    }
}

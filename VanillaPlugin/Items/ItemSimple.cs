using MinecraftClone3API.Items;

namespace VanillaPlugin.Items
{
    /// <summary>A plain non-placeable item (stick, ingot, gem, food, …) rendered from a 2D resource-pack
    /// sprite. The texture path is a full in-jar asset path, loaded lazily client-side.</summary>
    public class ItemSimple : Item
    {
        private readonly string _texturePath;

        public ItemSimple(string name, string texturePath) : base(name) => _texturePath = texturePath;

        public override string TexturePath => _texturePath;
    }
}

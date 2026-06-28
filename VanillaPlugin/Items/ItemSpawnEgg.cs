using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Items
{
    /// <summary>A creative spawn egg: right-clicking spawns one of its <see cref="EntityType"/> at the clicked
    /// cell. Entities are server-authoritative, so the spawn happens server-side; the inventory icon is the
    /// official spawn-egg sprite, and the display name resolves through the pack's i18n via its Minecraft id.</summary>
    public class ItemSpawnEgg : Item
    {
        private readonly EntityType _type;
        private readonly string _texturePath;
        private readonly string _minecraftId;

        public ItemSpawnEgg(EntityType type, string texturePath) : base(type.Name + "SpawnEgg")
        {
            _type = type;
            _texturePath = texturePath;
            _minecraftId = Identifier.FromResourcePath(texturePath);
        }

        protected override CreativeTab DefaultCreativeTab => CreativeTab.SpawnEggs;

        public override string TexturePath => _texturePath;
        public override string MinecraftId => _minecraftId;
        public override bool IsUsable => true;

        public override void OnUseServer(WorldServer world, EntityPlayer player, Vector3D<float> position)
            => world.SpawnEntity(_type, position);
    }
}

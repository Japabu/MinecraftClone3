using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using OpenTK.Mathematics;

namespace VanillaPlugin.Items
{
    /// <summary>A creative spawn egg: right-clicking spawns one of its <see cref="EntityType"/> at the clicked
    /// cell. Entities are server-authoritative, so the spawn happens server-side; the inventory icon is the
    /// official spawn-egg sprite.</summary>
    public class ItemSpawnEgg : Item
    {
        private readonly EntityType _type;
        private readonly string _texturePath;

        public ItemSpawnEgg(EntityType type, string texturePath) : base(type.Name + "SpawnEgg")
        {
            _type = type;
            _texturePath = texturePath;
        }

        public override string TexturePath => _texturePath;
        public override bool IsUsable => true;

        public override void OnUseServer(WorldServer world, EntityPlayer player, Vector3 position)
            => world.SpawnEntity(_type, position);
    }
}

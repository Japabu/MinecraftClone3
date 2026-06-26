using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace VanillaPlugin.Items
{
    /// <summary>An edible item: right-clicking it in survival refills hunger + saturation (server-authoritative)
    /// and consumes one from the stack. <see cref="_nutrition"/> / <see cref="_saturationModifier"/> are the
    /// Minecraft food values. Rendered from its 2D resource-pack sprite like <see cref="ItemSimple"/>.</summary>
    public class ItemFood : Item
    {
        private readonly string _texturePath;
        private readonly string _minecraftId;
        private readonly float _nutrition;
        private readonly float _saturationModifier;

        public ItemFood(string name, string texturePath, float nutrition, float saturationModifier) : base(name)
        {
            _texturePath = texturePath;
            _minecraftId = Identifier.FromResourcePath(texturePath);
            _nutrition = nutrition;
            _saturationModifier = saturationModifier;
        }

        public override string TexturePath => _texturePath;
        public override string MinecraftId => _minecraftId;

        public override bool IsUsable => true;
        public override bool ConsumesOnUse => true;

        public override bool CanUseServer(EntityPlayer player)
            => player.GameMode == GameMode.Survival && player.Hunger < PlayerSurvival.MaxHunger;

        public override void OnUseServer(WorldServer world, EntityPlayer player, Vector3 position)
            => PlayerSurvival.Eat(player, _nutrition, _saturationModifier);
    }
}

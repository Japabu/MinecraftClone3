using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Items
{
    /// <summary>
    /// A registered item type — the thing an <see cref="ItemStack"/> references by id. Most items are the
    /// auto-generated block form (<see cref="ItemBlock"/>, created when a block is registered); standalone
    /// items (sticks, ingots, tools) have no block and cannot be placed. The class is GL-free so the headless
    /// server uses it too; the 2D inventory sprite (<see cref="TexturePath"/>) is loaded lazily, client-side.
    /// </summary>
    public class Item : RegistryEntry
    {
        public Item(string name) : base(name)
        {
        }

        public ushort Id { get; internal set; }

        public virtual int MaxStackSize => 64;

        /// <summary>The item's Minecraft content id (e.g. <c>"minecraft:stick"</c>), used to resolve its name
        /// from the resource pack's translations and to match the pack's crafting recipes/tags. Null for items
        /// with no Minecraft equivalent.</summary>
        public virtual string MinecraftId => null;

        /// <summary>The block this item places, or null for a non-placeable item.</summary>
        public virtual Block GetBlock() => null;

        /// <summary>Whether right-clicking with this item triggers a server-side <see cref="OnUseServer"/>
        /// action (e.g. a spawn egg) instead of placing a block — lets the client send a use request only
        /// when one is meaningful.</summary>
        public virtual bool IsUsable => false;

        /// <summary>Server-side right-click action for a non-block item (the effect is server-authoritative).
        /// <paramref name="position"/> is the world cell the player clicked toward. No-op by default.</summary>
        public virtual void OnUseServer(WorldServer world, EntityPlayer player, Vector3 position) { }

        /// <summary>Resource-pack path of the 2D inventory sprite for a non-block item (e.g.
        /// <c>"minecraft:item/stick"</c>); null for block items, which render a 3D isometric icon instead.</summary>
        public virtual string TexturePath => null;

        public virtual string GetUnlocalizedName() =>
            MinecraftId != null
                ? Identifier.TranslationKey("item", MinecraftId)
                : I18N.UnlocalizedName(RegistryKey, "items");

        public virtual string GetName() => I18N.Get(GetUnlocalizedName());
    }
}

using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;

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

        /// <summary>The block this item places, or null for a non-placeable item.</summary>
        public virtual Block GetBlock() => null;

        /// <summary>Resource-pack path of the 2D inventory sprite for a non-block item (e.g.
        /// <c>"minecraft:item/stick"</c>); null for block items, which render a 3D isometric icon instead.</summary>
        public virtual string TexturePath => null;

        public virtual string GetUnlocalizedName() => I18N.UnlocalizedName(RegistryKey, "items");

        public virtual string GetName() => I18N.Get(GetUnlocalizedName());
    }
}

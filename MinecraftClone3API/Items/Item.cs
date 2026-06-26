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

        /// <summary>Whether right-clicking this item while aiming at an entity triggers <see cref="OnUseOnEntity"/>
        /// (e.g. shears on a sheep) — lets the client send an entity-targeted use only when it's meaningful.</summary>
        public virtual bool UsableOnEntity => false;

        /// <summary>Server-side right-click action against a targeted entity (server-authoritative). The target is
        /// resolved from the server's own entity list, so a request can't act on an arbitrary id. No-op by
        /// default.</summary>
        public virtual void OnUseOnEntity(WorldServer world, EntityPlayer player, Entity target) { }

        /// <summary>Whether a successful <see cref="OnUseServer"/> consumes one from the held stack (e.g. eating
        /// food). False for reusable items like spawn eggs.</summary>
        public virtual bool ConsumesOnUse => false;

        /// <summary>Whether <see cref="OnUseServer"/> changes the inventory without consuming (e.g. equipping
        /// armor swaps it into an armor slot), so the server re-syncs the inventory to the client afterwards.</summary>
        public virtual bool RefreshInventoryAfterUse => false;

        /// <summary>The armor slot this item equips into when right-clicked, or null for non-armor.</summary>
        public virtual ArmorSlot? ArmorSlot => null;

        /// <summary>Armor defense points (half-armor-icons) this piece grants while worn; 0 for non-armor.</summary>
        public virtual int ArmorDefense => 0;

        /// <summary>Server-side gate deciding whether <see cref="OnUseServer"/> may run for this player right now
        /// (e.g. food only when in survival with hunger to refill). True by default.</summary>
        public virtual bool CanUseServer(EntityPlayer player) => true;

        /// <summary>Melee damage (in half-hearts) dealt when left-clicking an entity while holding this item.
        /// Defaults to <see cref="EntityCombat.BaseHandDamage"/>; swords and other weapons raise it.</summary>
        public virtual float AttackDamage => EntityCombat.BaseHandDamage;

        /// <summary>The tool category this item acts as, or <see cref="ToolType.None"/> for non-tools. A held
        /// tool mines a block faster when this matches the block's <see cref="Block.PreferredTool"/>.</summary>
        public virtual ToolType ToolType => ToolType.None;

        /// <summary>The tool material's mining-speed multiplier (Minecraft: wood 2, stone 4, iron 6, diamond 8,
        /// gold 12), applied only when the tool matches the block's preferred tool. Ignored for non-tools.</summary>
        public virtual float MiningSpeed => 1f;

        /// <summary>The tool material's harvest tier (Minecraft: wood/gold 0, stone 1, iron 2, diamond 3). A
        /// block that <see cref="Block.RequiresCorrectTool"/> only mines at full speed when the matching tool's
        /// tier meets the block's <see cref="Block.RequiredToolTier"/>.</summary>
        public virtual int ToolTier => 0;

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

# Inventory, items & crafting

A creative-mode inventory: a 9-slot hotbar plus a 27-slot main inventory, every item available in infinite
supply, server-authoritative and persisted per player. The held hotbar item is what placement uses. Items are
a first-class registry (not just blocks), and a shaped/shapeless crafting system turns items into others.

## Item model (`Items/`, GL-free, shared)

- **`Item`** (`Items/Item.cs`) — a `RegistryEntry` with a ushort `Id`, a `MaxStackSize`, `GetBlock()` (the
  block it places, or null), a 2D inventory-sprite `TexturePath` (for non-block items), and `GetName()` (the
  localized display name). The base class is GL-free so the headless server uses it. A non-block item can set
  `IsUsable` and override `OnUseServer` for a server-side right-click action (e.g. Vanilla's `ItemSpawnEgg`
  spawns a creature — see [entities.md](entities.md)); right-clicking a usable item sends a `UseItemRequestPacket`.
- **`ItemBlock`** (`Items/ItemBlock.cs`) — the auto-generated item form of a block. Registering a block
  (`PluginContext.Register(Block)`) also registers an `ItemBlock` for it under the same registry key, so every
  block is an item: placeable (`GetBlock()` returns the block) and rendered as a 3D isometric icon.
- **Standalone items** subclass `Item` (e.g. Vanilla's `ItemSimple` — stick, coal, ingots, diamond, apple):
  no block, a 2D sprite, not placeable.
- **`ItemRegistry`** (`Util/ItemRegistry.cs`) — parallels `BlockRegistry` but with its **own id space** (item
  ids travel only in inventory packets, never in chunk storage). Id 0 is the empty stack; real items start at
  1. Ids are assigned in registration order, deterministic for a fixed plugin set. `GameRegistry` exposes
  `GetItem(id/key)`, `Items`, and `MatchRecipe(...)`.
- **`ItemStack`** (`Items/ItemStack.cs`) — value type: `ItemId` (ushort, `0` = empty), `Count`, `Metadata`
  (placement metadata: stair facing, glass tint). `Item` resolves the registered item. Being a struct means
  assignment is a deep copy — relied on when cloning inventories across the loopback transport.
- **`Inventory`** — `Slots[36]` (hotbar `0..8`, main `9..35`) + `SelectedHotbar`. `Write`/`Read` serialize it.

**Display names.** `I18N.UnlocalizedName(registryKey, category)` maps a `"Vanilla:Stone"` registry key to the
lang key `"vanilla.blocks.Stone"` (`"vanilla.items.Stick"` for items). `Block.GetUnlocalizedName` /
`Item.GetUnlocalizedName` use it, so adding a block/item only needs a matching `vanilla.<category>.<Name>` line
in `VanillaPlugin/Content/Lang/en-US.lang`.

## Crafting (`Items/`, GL-free engine; `Client/GUI/`, client logic + screens)

- **`CraftingRecipe`** (`Items/CraftingRecipe.cs`) — abstract; matches an N×N grid (row-major `ItemStack[]`)
  and yields a `Result`. `ShapedRecipe` (a trimmed pattern of item ids, placeable anywhere in the grid and
  horizontally mirrorable) and `ShapelessRecipe` (a multiset of ingredient ids filling the grid). Matching
  compares item ids only (metadata ignored). `Recipes.Shaped/Shapeless` (`Items/Recipes.cs`) build them from
  registry keys; `PluginContext.Register(CraftingRecipe)` registers them. `GameRegistry.MatchRecipe(grid,w,h)`
  returns the first matching result. Vanilla's recipes live in `VanillaPlugin/VanillaRecipes.cs` (planks,
  sticks, crafting table, oak stairs, torches, stone bricks).
- **`CraftingState`** (`Client/GUI/CraftingState.cs`) — shared client crafting logic: an N×N scratch grid plus
  the cursor-held stack, with the standard pick/place/swap slot interaction, the recipe result, ingredient
  consumption on take, and returning grid contents to the inventory on close. Player-inventory edits mutate
  the server-authoritative replica and mirror up via `WorldClient.SendInventoryAction`; the grid is pure local
  scratch.
- **3×3 crafting table** — `GuiCraftingTable` (`Client/GUI/GuiCraftingTable.cs`), opened by **right-clicking a
  crafting table block**, over the official `container/crafting_table.png` (placeholder slot frames without a
  pack). Shows the 3×3 grid, the result slot, and the full player inventory.
- **2×2 player crafting** — embedded in `GuiCreativeInventory` (the free area left of the item grid; Minecraft
  puts player crafting in the creative survival tab), reusing `CraftingState`.

**Right-click interaction.** `Block.OnActivated(window, world, pos, player)` (default false) lets a block
handle a right-click (and suppress placing the held item). It is **client-only** — `PlayerController` calls it
before `PlaceBlock`; the headless server never does — so a block may open a GUI there (`BlockCraftingTable`).

## Authority, networking, persistence

The server owns the inventory; the flow is in [networking.md](networking.md) (`InventoryState` /
`InventoryAction` / `HeldSlot`). On login `ServerNetwork` loads the player's saved inventory or seeds a creative
default (`SeedCreativeInventory` — the first nine placeable block items across the hotbar) and sends it as
`InventoryState`. `ClientSession.Inventory` is saved (`<worldDir>/Players/<name>.dat`) on disconnect and stop.

`WorldClient` keeps a local `Inventory` replica, copies the `InventoryState` it receives slot-by-slot, edits
optimistically, and sends `InventoryAction` / `HeldSlot` on changes. Inputs are trusted, not validated
(creative sandbox) — same stance as placement metadata.

> The `Login` packet carries the player name as the save key; the client currently sends an empty name, so a
> singleplayer world saves to `Players/player.dat`. Distinct per-player MP inventories need real player names.

## Rendering

- **3D isometric block icons** (`Client/Graphics/ItemIconRenderer.cs`) — each block is meshed once into the
  void `IconWorld` and drawn with the `ItemIcon` shader into a per-block `TextureFramebuffer`, cached by block
  id. Main-thread only (every step is a GL call). The framebuffer is GL bottom-left origin, so
  `ItemStackRenderer` flips V when blitting.
- **`ItemStackRenderer`** draws an `ItemStack` in a slot: a block item's 3D icon, or a standalone item's lazily
  loaded 2D sprite (`TexturePath`, cached, placeholder box when absent), plus a count label when above one. It
  re-asserts alpha blending before blitting because `GetIcon` may have just rendered (depth on, blend off).
- **`HotbarRenderer`** draws the always-on HUD hotbar from `widgets.png` (placeholder boxes without a pack).
- **`GuiCreativeInventory`** (overlay, **E**) — scrollable grid of every registered item over
  `creative_inventory/tab_items.png`, a cursor-held stack, the clickable hotbar row, and the 2×2 crafting panel.
- **`GuiTooltip`** (`Client/GUI/GuiTooltip.cs`) — the item-name tooltip drawn next to the cursor when hovering
  a non-empty slot; used by the creative and crafting-table screens.

**No Minecraft assets are shipped.** GUI/item textures load at runtime from the user's resource pack by asset
path (guarded by `ResourceReader.Exists`); absent a pack, screens draw placeholders.

## Input & placement

`PlayerController` handles hotbar selection (number keys `1`–`9`, scroll wheel — wrapping, mirrored via
`WorldClient.SendHeldSlot`). Right-click first tries `Block.OnActivated` on the targeted block (crafting table
→ opens its screen); otherwise `PlaceBlock` places the held item's block (skipping non-placeable items and
empty slots). Breaking is unchanged. The creative screen opens on **E** in `StateWorld.Update`.

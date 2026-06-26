# Inventory, items & crafting

A creative-mode inventory: a 9-slot hotbar plus a 27-slot main inventory, every item available in infinite
supply, server-authoritative and persisted per player. The held hotbar item is what placement uses. Items are
a first-class registry (not just blocks), and crafting recipes loaded from the resource pack turn items into
others.

## Item model (`Items/`, GL-free, shared)

- **`Item`** (`Items/Item.cs`) — a `RegistryEntry` with a ushort `Id`, a `MaxStackSize`, `GetBlock()` (the
  block it places, or null), a 2D inventory-sprite `TexturePath` (for non-block items), a `MinecraftId` (its
  `namespace:name` content id), and `GetName()` (the localized display name). The base class is GL-free so the
  headless server uses it. A non-block item can set `IsUsable` and override `OnUseServer` for a server-side
  right-click action (e.g. Vanilla's `ItemSpawnEgg` spawns a creature — see [entities.md](entities.md));
  right-clicking a usable item sends a `UseItemRequestPacket`. `CanUseServer(player)` gates whether the action
  may run (default true) and `ConsumesOnUse` (default false) decrements one from the held stack on success.
  This is the **food** path: Vanilla's `ItemFood` (Apple — MC nutrition 4 / saturation modifier 0.3) is usable,
  consumes-on-use, and `CanUseServer` only true in survival with hunger to refill;
  `ServerNetwork.ApplyUseRequest` checks `CanUseServer`, runs `OnUseServer` (`PlayerSurvival.Eat`), then
  decrements the slot and re-pushes `InventoryState`. Right-click already routes here, so no new packet.
- **`ItemBlock`** (`Items/ItemBlock.cs`) — the auto-generated item form of a block. Registering a block
  (`PluginContext.Register(Block)`) also registers an `ItemBlock` for it under the same registry key, so every
  block is an item: placeable (`GetBlock()` returns the block) and rendered as a 3D isometric icon.
- **Standalone items** subclass `Item` (e.g. Vanilla's `ItemSimple` — stick, coal, ingots, diamond; `ItemFood`
  — apple): no block, a 2D sprite, not placeable.
- **`ItemRegistry`** (`Util/ItemRegistry.cs`) — parallels `BlockRegistry` but with its **own id space** (item
  ids travel only in inventory packets, never in chunk storage). Id 0 is the empty stack; real items start at
  1. Ids are assigned in registration order, deterministic for a fixed plugin set. `GameRegistry` exposes
  `GetItem(id/key)`, `Items`, and `MatchRecipe(...)`.
- **`ItemStack`** (`Items/ItemStack.cs`) — value type: `ItemId` (ushort, `0` = empty), `Count`, `Metadata`
  (placement metadata: stair facing, glass tint). `Item` resolves the registered item. Being a struct means
  assignment is a deep copy — relied on when cloning inventories across the loopback transport.
- **`Inventory`** — `Slots[36]` (hotbar `0..8`, main `9..35`) + `SelectedHotbar`. `Write`/`Read` serialize it.

**Minecraft identity & display names.** Each block/item carries a `MinecraftId` (e.g. `"minecraft:stone"`,
`"minecraft:stick"`) — `BlockBasic` derives it from the model path and `ItemSimple` from the texture path;
custom blocks whose path doesn't match (water) set it explicitly. `Identifier` (`Util/Identifier.cs`) derives
ids from resource paths and builds the Minecraft translation key (`block.minecraft.stone`,
`item.minecraft.stick`). `Block`/`Item.GetName` resolve through that key, so names come straight from the
resource pack's `lang/*.json` (see [resources.md](resources.md)) — there is no hand-written Vanilla lang file.

## Crafting (`Items/`, GL-free engine; `Client/GUI/`, client logic + screens)

- **`CraftingRecipe`** (`Items/CraftingRecipe.cs`) — abstract; matches an N×N grid (row-major `ItemStack[]`)
  and yields a `Result`. `ShapedRecipe` (a trimmed pattern, placeable anywhere in the grid and horizontally
  mirrorable) and `ShapelessRecipe` (a multiset filling the grid). **Each ingredient cell is a *set* of
  acceptable item ids**, so a Minecraft tag (`#planks`) or an explicit list of alternatives both reduce to
  "any of these ids"; matching is a cheap id-set membership test (metadata ignored).
  `GameRegistry.MatchRecipe(grid,w,h)` returns the first matching result.
- **Recipes come from the resource pack** — `RecipeLoader` (`Items/RecipeLoader.cs`) runs once after all
  plugins load. It maps every Minecraft item id to the item we actually registered (by `MinecraftId`), then
  reads the pack's `data/<ns>/recipe/*.json` (the Minecraft data format), resolving ingredient tags from
  `data/<ns>/tags/item/*.json` (recursively). A recipe registers **only when its result and every ingredient
  cell resolve to at least one item we have**, so a pack with thousands of recipes contributes exactly those
  usable with the registered content (planks, sticks, crafting table, oak stairs, torches, stone bricks, and
  the smelts below with the current Vanilla set). Shaped/shapeless crafting **and** `minecraft:smelting`
  (plain-furnace) recipes are loaded; blasting/smoking/stonecutting/etc. are ignored.
- **Smelting** — `SmeltingRecipe` (`Items/SmeltingRecipe.cs`) is a single input id-set → result with a
  `CookingTime` (ticks); `GameRegistry.MatchSmelting(input)` returns the recipe a stack satisfies. Furnace
  **fuel** burn-times aren't in the pack (vanilla hardcodes them), so `FurnaceFuel` (`Items/FurnaceFuel.cs`)
  holds the table, built by `RecipeLoader` by resolving vanilla selectors (`#planks`, `#logs`, coal, …) against
  the items we have. With the current content: iron/gold ore → ingot and cobblestone → stone, fuelled by
  coal/logs/planks/sticks.
- **`CraftingState`** (`Client/GUI/CraftingState.cs`) — the backing state of a crafting grid: the N×N scratch
  grid (local, not part of the inventory), the recipe result, consuming one batch's ingredients
  (`ConsumeOne`), and returning leftovers to the inventory on close. The cursor and all slot interaction live
  in `ContainerScreen`. Player-inventory edits mutate the server-authoritative replica and mirror up via
  `WorldClient.SendInventoryAction`.
- **3×3 crafting table** — `GuiCraftingTable` (`Client/GUI/GuiCraftingTable.cs`), opened by **right-clicking a
  crafting table block**, over the official `container/crafting_table.png` (placeholder slot frames without a
  pack). The 3×3 grid + result slot + the full player inventory. The creative inventory has **no** crafting
  grid (crafting is the table's job, matching vanilla creative).

## Furnace (a server-authoritative container block)

Unlike the crafting table (a purely client-side scratch grid), a furnace has **persistent, server-owned state**
that **ticks even with the screen closed** — so it's the engine's first *block entity* and first *networked
container*. The pieces:

- **State** — `BlockDataFurnace` (`VanillaPlugin/BlockDatas/`) is a `ContainerBlockData`
  (`Blocks/ContainerBlockData.cs`): facing + three `ItemStack` slots (input/fuel/output) + burn/cook counters,
  persisted with the chunk like any `BlockData`. `ContainerBlockData` is the API's generic view of a synced
  container (`Slots`, `SetSlot`, `SyncFields`), so the networking never needs to know the concrete furnace type.
- **Smelting tick** — `BlockFurnace.OnServerTick` (run by the server's ticking-block registry, see
  [world-model.md](world-model.md)) is the vanilla algorithm: light fresh fuel when there's something to smelt,
  burn it down, advance the cook timer, and on completion consume one input → add one result. While burning it
  emits light and renders the **lit** model; lit↔unlit transitions go through `SetBlockData` (remesh + relight),
  while per-tick progress only flags the chunk for saving.
- **Sync & interaction** — opening the screen sends `OpenContainerPacket`; while open the server streams the
  furnace's `Slots`+`SyncFields` each tick as a `ContainerStatePacket`, which the client mirrors into a
  `ContainerView` (`Client/Blocks/`). Moving items in the three furnace slots sends `ContainerSlotPacket`
  (trusted, like inventory edits); the player-inventory rows still use `SendInventoryAction`. Closing sends
  `CloseContainerPacket`. (See [networking.md](networking.md).)
- **Screen** — `GuiFurnace` (`Client/GUI/GuiFurnace.cs`) over `container/furnace.png`: input/fuel/output slots
  + the player inventory, with the lit flame (burn remaining, fills bottom-up) and cook arrow (fills
  left-to-right) drawn from the sheet's progress sprites. Output is an `IsOutput` slot (take-only); shift-click
  moves furnace↔inventory (smeltables to input, fuels to fuel). No XP (the engine has no XP system).

**Right-click interaction.** `Block.OnActivated(window, world, pos, player)` (default false) lets a block
handle a right-click (and suppress placing the held item). It is **client-only** — `PlayerController` calls it
before `PlaceBlock`; the headless server never does — so a block may open a GUI there (`BlockCraftingTable`).

## Container screens & slot interaction (`Client/GUI/ContainerScreen.cs`, `Slot.cs`)

`ContainerScreen` is the shared base for the crafting-table and creative screens. It owns the cursor-held
stack and implements the **full vanilla slot interaction** against the `Slot`s a subclass declares; a `Slot`
is a GUI-space rect plus get/set accessors onto wherever the stack lives (a grid cell, a server-mirrored
inventory slot, a crafting result, or a creative infinite source):

- **Left-click** — pick up / place / swap / merge same-item stacks (up to max stack).
- **Right-click** — pick up half (ceil) from a slot, or place one onto an empty/matching slot.
- **Left-drag** across ≥2 slots — distribute the held stack evenly, remainder back to the cursor.
- **Right-drag** across slots — one item per slot.
- **Double-click** (left) — gather all matching items from the container into the held stack, up to a full
  stack; partial stacks are consumed first so full ones stay intact. Handled generically in `ContainerScreen`
  (`GatherToCursor`), timed by a 250 ms `Stopwatch` on the same slot.
- **Shift-click** — quick-move a stack to the other region. Routing is per-screen via `OnShiftClick` using the
  `MergeInto`/`SlotsInGroup` helpers and each slot's `Group` tag: the crafting table moves grid→inventory and
  main↔hotbar (and the output crafts the full batch); the creative screen grants a full stack from the item
  list to the hotbar and trashes a shift-clicked hotbar slot.
- **Output slot** — can't be placed into; taking crafts one batch (`OnTakeOutput` → `ConsumeOne`); merges with
  a matching cursor.
- **Source slot** (creative item list) — left-click takes a full stack, right-click one; dropping a held stack
  onto it discards it (vanilla trash behavior).

Clicks resolve on button release so a press-move-release can become a drag; a press+release on one slot is a
normal click. Inventory `Slot.Set` closures write the replica and call `SendInventoryAction`, so every edit
syncs to the server automatically.

**Render feedback** matches vanilla: the slot under the cursor gets the translucent-white hover overlay
(`0x80FFFFFF`, `DrawSlotHighlight`). While painting a drag, that same overlay marks each painted slot and the
deposited items already appear *in place* — `ComputeDragDistribution` (shared with the release path so preview
and result agree) drives both the in-slot counts and the reduced count shown on the carried cursor.

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
- **`HotbarRenderer`** draws the always-on HUD hotbar from the modern sprite layout (`hud/hotbar.png` +
  `hud/hotbar_selection.png`; placeholder boxes without a pack — see the sprite-layout note below).
- **`GuiCreativeInventory`** (overlay, **E** in creative) — scrollable grid of every registered item over
  `creative_inventory/tab_items.png`, a cursor-held stack, and the clickable hotbar row.
- **`GuiInventory`** (overlay, **E** in survival) — the survival player inventory over
  `container/inventory.png`: a 2×2 crafting grid + result (scratch, returns to the inventory on close), the 3×9
  main inventory, and the hotbar. Same `ContainerScreen` interaction as the crafting table; armor / offhand /
  the 3D player preview are not modelled.
- **`GuiTooltip`** (`Client/GUI/GuiTooltip.cs`) — the item-name tooltip drawn next to the cursor when hovering
  a non-empty slot; used by both container screens.

**No Minecraft assets are shipped.** GUI/item textures load at runtime from the user's resource pack by asset
path (guarded by `ResourceReader.Exists`); absent a pack, screens draw placeholders.

## Input & placement

`PlayerController` handles hotbar selection (number keys `1`–`9`, scroll wheel — wrapping, mirrored via
`WorldClient.SendHeldSlot`). Right-click first tries `Block.OnActivated` on the targeted block (crafting table
→ opens its screen); otherwise `PlaceBlock` places the held item's block (skipping non-placeable items and
empty slots). Breaking is creative-instant / survival hold-to-mine (see [state-gameloop.md](state-gameloop.md)).
The Inventory key (**E**) opens `GuiCreativeInventory` in creative, `GuiInventory` in survival
(`StateWorld.Update`).

**HUD/GUI sprite layout.** A current resource pack (1.20.2+) uses **individual sprite PNGs** under
`gui/sprites/hud/…` and `gui/sprites/…` rather than the old monolithic `widgets.png`/`icons.png`. `GuiAssets`
holds the per-sprite asset paths (hotbar, hotbar selection, hearts, food); each is loaded lazily and null-guarded
so a missing pack falls back to placeholder drawing.

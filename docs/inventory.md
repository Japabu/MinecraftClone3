# Inventory & items

A creative-mode inventory: a 9-slot hotbar plus a 27-slot main inventory, every block available in infinite
supply, server-authoritative and persisted per player. The held hotbar item is what placement uses.

## Data model (`Items/`, GL-free, shared)

- **`ItemStack`** — a value type (`struct`): `BlockId` (ushort, an item *is* a block reference; `0` = empty),
  `Count`, `Metadata`. `IsEmpty`, `SameItem`, `WithCount`, and `Write`/`Read` for the wire. Being a struct
  means assignment is a deep copy — relied on when cloning inventories across the loopback transport.
- **`Inventory`** — `Slots[36]` (hotbar `0..8`, main `9..35`) + `SelectedHotbar`. `SelectedItem` is
  `Slots[SelectedHotbar]`. `Write`/`Read` (selected index + every slot) serialize the whole thing.

These live in the API library and touch no GL, so the headless server uses them directly.

## Authority, networking, persistence

The server owns the inventory; the flow is in [networking.md](networking.md) (`InventoryState` /
`InventoryAction` / `HeldSlot`). On login `ServerNetwork` loads the player's saved inventory
(`PlayerSerializer.Load`) or seeds a creative default (`SeedCreativeInventory` — the first nine non-air
blocks across the hotbar) and sends it as `InventoryState`. `ClientSession.Inventory` is saved
(`PlayerSerializer.Save`, `<worldDir>/Players/<name>.dat`) on disconnect and on server stop.

`WorldClient` keeps a local `Inventory` replica, copies the `InventoryState` it receives slot-by-slot, edits
optimistically, and sends `InventoryAction` / `HeldSlot` on changes. Inputs are trusted, not validated
(creative sandbox) — same stance as placement metadata.

> The `Login` packet carries the player name as the save key; the client currently sends an empty name, so a
> singleplayer world saves to `Players/player.dat`. Distinct per-player MP inventories need real player names.

## Rendering

- **3D isometric block icons** (`Client/Graphics/ItemIconRenderer.cs`) — each block is meshed once (via the
  normal `ChunkMesher.AddBlockToVao`) into the void `IconWorld` (every neighbour reads as air so all six faces
  survive culling; light reads full), then drawn with the `ItemIcon` shader into a per-block
  `TextureFramebuffer`, cached by block id. The shader forward-shades with a fixed per-face brightness (top
  brightest), matching Minecraft's icon look — no G-buffer, no world light. Lazy and **main-thread only**
  (every step is a GL call); the GUI calls `GetIcon` while drawing. The framebuffer is GL bottom-left origin,
  so `ItemStackRenderer` flips V when blitting it into the top-left GUI space.
- **`ItemStackRenderer`** draws an `ItemStack` in a slot (icon + count when >1). It re-asserts alpha blending
  before the blit because `GetIcon` may have just rendered an icon (depth on, blend off).
- **`HotbarRenderer`** draws the always-on HUD hotbar from the official `widgets.png` (the 182×22 strip + the
  24×24 selection cursor), falling back to placeholder boxes when no resource pack is present.
- **`GuiCreativeInventory`** (overlay, opened with **E**) — a scrollable 9×5 grid of every registered block
  over the official `creative_inventory/tab_items.png`, a cursor-held stack, and a bottom hotbar row the
  player fills by clicking. Picking from the grid is infinite; clicking a hotbar slot swaps it with the
  cursor and sends an `InventoryAction`. Closes on E/Escape, restoring the grabbed cursor.

**No Minecraft assets are shipped.** GUI textures load at runtime from the user's resource pack by asset path
(`GuiAssets`, guarded by `ResourceReader.Exists`); absent a pack, the HUD/screen draw placeholders.

## Input & placement

`PlayerController` handles hotbar selection (number keys `1`–`9`, scroll wheel — wrapping) and mirrors a
change up via `WorldClient.SendHeldSlot`. `PlaceBlock` places `Inventory.SelectedItem`'s block (skipping when
empty); breaking is unchanged. The creative screen opens on **E** in `StateWorld.Update`.

# State system & game loop

`StateEngine` (static) holds a stack of `_states` plus `_overlays`. An open overlay unfocuses the base state
(it still updates, but ignores input). It does **not** by itself pause the world: only an overlay whose
`PausesWorld` is true (the Esc `GuiPauseMenu`) freezes the singleplayer simulation — `StateEngine.WorldPaused`
is recomputed from the overlay stack each `Update`, and `StateWorld` gates `simulate` on it. So a
container/inventory/furnace screen leaves the integrated server ticking (furnaces keep smelting, mobs keep
moving), exactly as vanilla; multiplayer never pauses (can't pause a shared server).
`ReplaceState(state)` is **deferred to end of frame** and calls `Exit()` on every removed
layer — that's how a world saves on "Save and Quit to Title" and on window close
(`GameClient.OnUnload → StateEngine.Exit`).

**World teardown is asynchronous.** `StateWorld.Exit` runs only the GL cleanup (`WorldClient.Disconnect` — VAO/
buffer disposal) on the main/GL thread; stopping the server network and joining + saving the world (GL-free,
but seconds-long when many chunks are dirty — a burning furnace floods light and dirties every chunk it
touches) runs on a foreground `World Teardown` thread. Blocking the render thread for that save would leave
OpenTK unable to draw or pump events; on macOS the window then goes unresponsive and its OpenGL-over-Metal
drawable **wedges**, freezing the on-screen image on the last frame even though the game has moved on. "Save and
Quit to Title" therefore routes through `GuiSavingWorld`, which keeps drawing a "Saving world..." screen each
frame and `ReplaceState`s to `GuiMainMenu` once `StateWorld.IsTearingDown` clears. Opening another world calls
`StateWorld.WaitForPendingTeardown()` first (and the foreground thread keeps the process alive to finish a save
on app exit), so a save never races the next world on disk. State flow:

```
GuiResourceLoading ──(done)──▶ GuiMainMenu ──Multiplayer──────────────────────────▶ StateWorld(window, multiplayer:true)
                                    ▲ │ │ Options                                        │ Esc
                                    │ │ ▼                                                ▼
                                    │ │ GuiOptions (overlay) ◀──────── Options ── GuiPauseMenu (overlay)
                                    │ │   └▶ GuiGraphicsOptions / GuiControls (overlays)
                                    │ └────────── Save & Quit ◀──────────────────────────┘
                                    │ Singleplayer
                                    ▼                  Create New World
                              GuiWorldSelection ◀──────────────────────▶ GuiCreateWorld
                                    │  │  ▲ Back/Esc                          │ Create / Cancel/Esc
                       Play/dbl-click│  │ Delete                             ▼
                                    ▼  │ (GuiConfirm overlay ── Yes ──▶ delete + rebuild)
                              StateWorld(window, world)  ◀──────────────────┘
```

`StateWorld` has two public ctors over a shared private one: `StateWorld(window, WorldInfo)` (SP — runs that
world's folder in a loopback+integrated `WorldServer(seed, worldDir)`) and `StateWorld(window, multiplayer)`
(a `TcpConnection` for MP). It creates the `WorldClient` and logs in; on a failed MP connect it flips back to
the main menu. It then sits in a `_loading` phase (pumping server/network/world + drawing a loading screen
with a **progress bar**) until the join handshake completes before running player input. `RenderLoading` eases
a bar toward `LoadProgressTarget()` — staged by the handshake (connecting → spawn region streaming in, paced by
the streamed-chunk count → ready), capped below full until the player can actually drop in.

**World selection (singleplayer).** "Singleplayer" opens `GuiWorldSelection` (lists worlds under `Worlds/`,
sorted last-played-first via `WorldManager`), from which the player plays, creates, or deletes a world.
`GuiCreateWorld` takes a name + optional seed (blank → random, numeric → used directly, else
`WorldGenRandom.StableHash`). These are **states** (navigated by `ReplaceState`), not overlays, because
`GuiCreateWorld` owns `GuiTextInput`s that subscribe to the window's `TextInput` event and must `Detach()` in
`Exit()` — and `ReplaceState` calls `Exit()` on removed layers while dead **overlays** are dropped *without*
`Exit()`. The delete confirmation (`GuiConfirm`) owns no text input, so it is a safe overlay.

**`GuiTextInput`** (`Client/GUI/GuiTextInput.cs`) is the reusable single-line text field: chars arrive via
the window `TextInput` event (OS handles layout/shift), a left click sets focus by whether it landed inside
(so clicking elsewhere defocuses), Backspace deletes via `IsKeyPressed`. **Held-key repeat is not
implemented** (one char per key press); the owning state must call `Detach()` from `Exit()` to unsubscribe.

**Player movement & physics** (`Entities/PlayerController.cs` + `PlayerPhysics.cs`, client-only, main
thread). The player is a **0.6 × 1.8 AABB**; `Entity.Position` is the **feet** and the camera renders at
`Position + EyeOffset` (1.62) via `Entity.RenderPosition`/`EyeOffset` (defaults keep non-player entities a
point). `PlayerController` is split into `UpdateFrame` (per frame: look, fly toggle, hotbar selection, debug keys,
break/place — see [inventory.md](inventory.md)) and `Tick` (one fixed **20 tps** step), driven by
`StateWorld`'s accumulator; the **Inventory** keybind (default **E**) opens the creative inventory overlay
(`StateWorld.Update`; no crafting grid, matching vanilla creative), and the **Drop** keybind (default **Q**,
Ctrl+Q for the whole stack) throws the held item (`WorldClient.SendDropItem` → server, see
[entities.md](entities.md)). Right-click first tries `Block.OnActivated` on the targeted block (a **crafting
table** opens the 3×3 `GuiCraftingTable` overlay) and only places the held block if no block handled it.
`ApplyInterpolation(alpha)` lerps `PrevPosition→Position` so 20 tps motion is smooth at the frame rate. Two
modes, toggled by **double-tapping Space**:
- **Walk (default):** exact-Minecraft constants integrated once per tick — gravity `v_y=(v_y−0.08)·0.98`,
  jump `0.42`, ground accel `0.1`/friction `0.546`, air `0.02`/`0.91`, Ctrl sprint `1.3×`.
  `PlayerPhysics.MoveWithCollision` is **swept per-axis (Y→X→Z)**: it clips each axis's displacement against
  overlapping solid blocks' collision boxes and zeroes the blocked component. A block contributes its boxes
  via **`Block.GetCollisionBoxes`** (block-local, ±0.5), which defaults to the single `GetBoundingBox` cube
  but lets a block return **several** boxes — stairs return an L (slab + step). `GetBoundingBox` stays a
  single cube and is used for *raytrace/targeting + outline* only, so targeting a stair is a whole-cube hit.
  `OnGround` is a **velocity-independent downward probe** (`ClipY(box, −GroundProbe)` clipped ⇒ grounded),
  not the Y-clip outcome — so a tick entering with `Velocity.Y==0` (spawn, just un-flew) or landing flush
  doesn't read airborne for a tick. **Auto-step:** when grounded, **not rising** (`velY ≤ 0`), and a
  horizontal axis is blocked, the move is retried raised by `StepHeight` (0.6 = MC) and kept only if it
  advanced farther — climbs slabs/partial blocks, still needs a jump for a full cube. The not-rising gate
  matters: on the jump tick `velY = +0.42`, and stepping then would stack `StepHeight` on the jump and clip
  the player up a full block — so stepping only happens while settling onto the ground.
- **Swim (in water):** when the body overlaps a `Block.IsLiquid` block (`PlayerPhysics.IsInLiquid` samples
  the lower + mid body), the walk tick takes the water branch — gentle water accel, all velocity damped by
  `WaterDrag`, **Space buoys up** (`SwimImpulse`), otherwise a slow sink (`WaterGravity` ≪ land gravity).
  Liquid is pass-through, so swept collision + the ground probe still run; you don't fall through.
- **Fly (creative):** the same fixed-step `Entity.Move` — Space/Shift up/down, Ctrl fast, **no
  gravity/collision** (noclip). Also runs in the 20 tps tick and is render-interpolated like walking.

The block-target raytrace uses the **eye** (`RenderPosition + EyeOffset`); `SendMove` ships the **feet**
position, so remote players (drawn by `EntityRenderer` as 0.6×1.8 boxes, offset up by half-height) line up.
**Remote entities are render-interpolated:** positions arrive at 20 tps, so `Entity.SetInterpTarget` (per
`EntityMove`) aims a lerp from the current visual position toward the new target, advanced per frame by
`WorldClient.UpdateEntityInterpolation`, and `Entity.RenderPosition` returns the lerp.

**Keybinds.** Movement/interaction keys are rebindable via `Keybinds` (`Client/Keybinds.cs`), a static
`GameAction → Keys` map persisted to `GamePaths.KeybindsFile` (`Keybinds.json`), loaded in `Program.Main`
alongside `GraphicsSettings`. `PlayerController` (and `StateWorld`/`ContainerScreen` for the Inventory action)
read the live binding each frame via `Keybinds.IsDown`/`IsPressed`, so a rebind takes effect immediately.
`GuiControls` (`States/GuiControls.cs`, an overlay opened from a "Controls..." button **inside the parent
`GuiOptions` screen** — see Graphics options below) lists each action: clicking a row arms it and the next key press rebinds it (Escape
cancels the arm), plus a "Reset to Defaults". The fixed debug keys (F1/F3/F4/F7/F10, number-key hotbar, Ctrl
drop-all modifier) stay hardcoded.

**Options.** The "Options" button (on `GuiMainMenu` and the `GuiPauseMenu` overlay) opens `GuiOptions`
(`States/GuiOptions.cs`), a small overlay menu that branches into the per-category sub-screens —
`GuiGraphicsOptions` ("Graphics...") and `GuiControls` ("Controls...") — each pushed as a further overlay.
Everything here is an **overlay** — each draws over whichever screen opened it and closing it (Done/Escape)
reveals that screen again, so opening options from the pause menu doesn't tear down the world.

`GuiGraphicsOptions` is the video-settings screen. Each control mutates `GraphicsSettings`
(`Client/GraphicsSettings.cs`), a static holder persisted to `GamePaths.GraphicsSettingsFile`. The widget
toolkit is **`GuiButton`** (cycles a discrete value, relabelling itself) and **`GuiSlider`** (a drag slider;
`onChange` fires only when the snapped value changes). Controls:
- **VSync** (Off/On/Adaptive) and **Fullscreen** (On/Off) — buttons; setters push window-level state onto the
  live `ClientResources.Window`.
- **Shadows** — a button cycling a **`ShadowQuality`** enum (Off/Low/Medium/High). It drives
  `WorldRenderer.ShadowDistance` (96/160/256) **and** `ShadowMapSize` (512/1024/2048);
  `WorldRenderer.EnsureShadowMap()` recreates `ClientResources.ShadowFramebuffer` (GL, in the shadow pass)
  when the size changes, and `uShadowMapTexel` (1/mapSize) is uploaded so the PCF disc tracks the resolution.
  `GraphicsSettings.ShadowsEnabled` (≠ Off) gates the passes + `uShadowsEnabled`.
- **Render Distance** (slider, 4–24 chunks) — the flagship knob. The five coupled radii are derived from this
  one setting so the `load ≥ send ≥ render`, `cache > send` chain can't be violated: client draw =
  `WorldRenderer.RenderDistance` (a computed property reading the setting live); **singleplayer**
  `StateWorld.ApplyRenderDistance` also drives `ServerNetwork.ViewDistance` (= chunks·16),
  `WorldServer.TerrainRadius` (= chunks+1, volatile, read live), and `WorldClient.CacheDistance` (= chunks·16
  + `CacheHysteresis` 80; its setter resets the evict gate so a *decrease* evicts immediately).
  **Multiplayer** drives only the client draw distance (the client can't exceed what the remote server
  streams; a `LoginAccept` view-distance advertise+clamp is deferred). `StateWorld.Update` re-applies on
  change.
- **FOV** (slider 30–110°), **Sensitivity** (slider, the mouse-delta multiplier), **Brightness** (slider
  0–0.3 → `uMinLight` in `Composition.fs`, the unlit floor).
- **LOD Quality** (slider 50–200%, `GraphicsSettings.LodHorizonQuality`) — scales how far the Phase-2
  horizon's **detail rings** (stride-4 → 8 → 16) extend before coarsening: higher = finer horizon farther out
  (lower FPS), lower = coarser/cheaper. Does **not** touch render distance (chunks there are always full
  detail). `WorldClient.MeshStepFor` reads it live; a change calls `WorldClient.ForceLodMeshRescan()`
  (`StateWorld.Update`) to re-step the existing LOD regions. Default 100%.
- **LOD Horizon** (slider 0–96 chunks, `GraphicsSettings.LodHorizonChunks`) — how many chunks of cheap LOD
  extend past the render distance (0 = horizon dormant / Phase-2 fully off). Drives `StateWorld.LodRingChunks`
  → `ApplyRenderDistance`, which sets `_world.LodRenderDistance` (the server gen ring / stream cull / client
  draw+cache radii) **and raises the projection far plane to clear `LodRenderDistance`**. A change re-applies
  the radius chain (no chunk remesh — LOD columns stream/evict at the new radius). Default 64.

`Program.Main` calls `GraphicsSettings.Load()` before creating the window and seeds `NativeWindowSettings`
from it, so the window opens with the saved vsync/fullscreen choice; the rest are read live each frame.
Numeric setters clamp to the `Min*/Max*` consts. The dedicated server never touches `GraphicsSettings`.

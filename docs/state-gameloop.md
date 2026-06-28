# State system & game loop

**The window & frame loop.** `Program.Main` creates a **Silk.NET** window (`Window.Create` with
`GraphicsAPI.None` — WebGPU owns its own surface/swapchain, so Silk must not stand up a GL context) and wires
the `Load`/`Update`/`Render`/`Closing` callbacks. `OnLoad` (run once the native window exists) brings up the
GPU device + surface (`Gpu.Init(new GpuContext(window))` → `Renderer.Load`), creates the event-driven
`InputManager` from `window.CreateInput()`, publishes both onto `ClientResources.Window`/`ClientResources.Input`,
hands the input to `StateEngine.AttachInput`, and pushes the first state (`GuiResourceLoading`). VSync is the
wgpu surface present mode (`Renderer.SetVSync`), **not** a window flag — `Off`→Immediate, `Adaptive`→Mailbox,
`On`→Fifo.

`OnUpdate` is the fixed update (just `StateEngine.Update()`). `OnRender` is the frame:
`Renderer.BeginFrame()` acquires the swapchain texture and opens the frame command encoder (returns **false**
during a resize/minimize when the surface can't be acquired — the frame is skipped rather than recorded into a
dead encoder); states then **record** their draws into that encoder via `StateEngine.Render()` instead of
issuing them against a default framebuffer; `Renderer.EndFrame()` opens the surface render pass, tonemaps the
HDR scene target (`Renderer.HdrScene`, rgba16float) into it when a world drew (`MarkSceneRendered`), flushes the
GUI sprite batch over the top, and presents. Frame/update/present timings feed the `Profiler` and `RenderDebug`
HUD. The benchmark/inspect automated modes drive the same loop and `Close()` the window when their scripted run
finishes; `OnClosing` stops the profiler, runs `StateEngine.Exit` (so the open world saves), and `Gpu.Shutdown`.

`StateEngine` (static) holds a stack of `_states` plus `_overlays`. An open overlay unfocuses the base state
(it still updates, but ignores input). It does **not** by itself pause the world: only an overlay whose
`PausesWorld` is true (the Esc `GuiPauseMenu`) freezes the singleplayer simulation — `StateEngine.WorldPaused`
is recomputed from the overlay stack each `Update`, and `StateWorld` gates `simulate` on it. So a
container/inventory/furnace screen leaves the integrated server ticking (furnaces keep smelting, mobs keep
moving), exactly as vanilla; multiplayer never pauses (can't pause a shared server).
`ReplaceState(state)` is **deferred to end of frame** and calls `Exit()` on every removed
layer — that's how a world saves on "Save and Quit to Title" and on window close
(`Program.OnClosing → StateEngine.Exit`).

**World teardown is asynchronous.** `StateWorld.Exit` runs only the GPU cleanup (`WorldClient.Disconnect` —
buffer/VAO disposal) on the main thread; stopping the server network and joining + saving the world (GPU-free,
but seconds-long when many chunks are dirty — a burning furnace floods light and dirties every chunk it
touches) runs on a foreground `World Teardown` thread. Blocking the render thread for that save would leave
Silk unable to draw or pump events; on macOS the window then goes unresponsive and its WebGPU drawable surface
**wedges**, freezing the on-screen image on the last frame even though the game has moved on. "Save and
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

**Input.** `InputManager` (`Client/Input/InputManager.cs`) wraps Silk's `IKeyboard`/`IMouse` directly, with no
intermediate snapshot structs — Silk's `Key`/`MouseButton` are used as-is. **Discrete** actions are
event-driven: it forwards Silk's `KeyDown`/`KeyUp`/`CharTyped`/`MouseDown`/`MouseUp`/`Scroll`, and
`StateEngine.AttachInput` (subscribed once at startup) routes each to the **foreground** layer (the top open
overlay, else the top state) — `OnKeyDown`/`OnCharTyped`/`OnMouseDown`/`OnMouseUp`/`OnScroll`. **Continuous**
input is pulled, not snapshotted: `IsKeyDown(Key)`/`IsMouseDown(MouseButton)` read Silk's live held state (for
movement), and `ConsumeMouseDelta()` returns the look delta accumulated from every sub-frame `MouseMove` since
the last call (the player controller drains it once per frame; `ResetMouseDelta`/`ResetMouse` drop the next
delta after re-grabbing the cursor so the camera doesn't snap). Cursor capture is `InputManager.CursorMode`
(`ClientResources.Input.CursorMode`): menus use `CursorMode.Normal`, a world grabs it with `CursorMode.Raw`.

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
`WorldGenRandom.StableHash`). It is a full-screen **state** (navigated by `ReplaceState`) that replaces the
world-selection screen; its `GuiTextInput`s receive characters as the **foreground** layer (no global
subscriptions to attach or tear down), so it could equally be an overlay — `ReplaceState` is just the natural
fit for "replace this menu with that one". The delete confirmation (`GuiConfirm`) is an overlay over the
selection screen.

**`GuiTextInput`** (`Client/GUI/GuiTextInput.cs`) is the reusable single-line text field: chars arrive via the
foreground layer's `OnCharTyped` (Silk's `KeyChar`, so the OS handles layout/shift; control chars are
dropped), a left click sets focus by whether it landed inside (so clicking elsewhere defocuses), Backspace
deletes via `OnKeyDown`. **Held-key repeat is not implemented** (one char per key press). Because input routes
through the foreground layer rather than a global subscription, the field needs no attach/detach lifecycle.

**Player movement & physics** (`Entities/PlayerController.cs` + `PlayerPhysics.cs`, client-only, main
thread). The player is a **0.6 × 1.8 AABB**; `Entity.Position` is the **feet** and the camera renders at
`Position + EyeOffset` (1.62) via `Entity.RenderPosition`/`EyeOffset` (defaults keep non-player entities a
point). **F5** cycles the camera perspective (`PlayerController.Perspective`): first-person → third-person
behind → third-person facing; the two third-person views trail/face the eye by 4 blocks, pulled in by a
block raytrace so the view never clips into terrain, and the renderer draws the player's own body
(`PlayerController.RenderSelf`). In first-person the held item renders as a viewmodel in the lower-right
(`HeldItemRenderer`, see [rendering.md](rendering.md)); attack/mine/place/use call `PlayerController.Swing()`,
which drives a swing arc (`SwingPhase`, advanced each frame; mining loops it). `PlayerController` is split into `UpdateFrame` (per frame: look, fly toggle, hotbar selection, debug keys,
break/place — see [inventory.md](inventory.md)) and `Tick` (one fixed **20 tps** step), driven by
`StateWorld`'s accumulator; the **Inventory** keybind (default **E**) opens the creative inventory overlay
(`StateWorld.Update`; no crafting grid, matching vanilla creative), and the **Drop** keybind (default **Q**,
Ctrl+Q for the whole stack) throws the held item (`WorldClient.SendDropItem` → server, see
[entities.md](entities.md)). Right-click first tries `Block.OnActivated` on the targeted block (a **crafting
table** opens the 3×3 `GuiCraftingTable` overlay) and only places the held block if no block handled it.
`ApplyInterpolation(alpha)` lerps `PrevPosition→Position` so 20 tps motion is smooth at the frame rate. Two
modes, toggled by **double-tapping Space**:
- **Walk (default):** exact-Minecraft constants integrated once per tick — gravity `v_y=(v_y−0.08)·0.98`,
  jump `0.42`, ground accel `0.1`/friction `0.546`, air `0.02`/`0.91`, sprint `1.3×`.
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
  the lower + mid body), the walk tick takes the water branch — gentle water accel (faster while
  sprint-swimming), all velocity damped by `WaterDrag`, **Space buoys up** (`SwimImpulse`), otherwise a slow
  sink (`WaterGravity` ≪ land gravity). Liquid is pass-through, so swept collision + the ground probe still
  run; you don't fall through.
- **Climb (on a ladder):** when the body overlaps a `Block.IsClimbable` block (`PlayerPhysics.IsOnLadder`), the
  walk tick clamps horizontal speed so you cling and overrides the vertical motion just before the move — a
  movement key or jump climbs up (`LadderClimbSpeed`), otherwise you slide down slowly (`LadderDescendSpeed`).
- **Fly (creative):** the same fixed-step `Entity.Move` — Space/Shift up/down, Ctrl fast, **no
  gravity/collision** (noclip). Also runs in the 20 tps tick and is render-interpolated like walking.

**Sprinting** (`PlayerController.UpdateSprint`, per frame). Engages on a held **Sprint** key (default Ctrl) or
a **Forward double-tap** while pushing forward; it is **sticky** (stays on with Forward held alone) until
Forward is released, a wall is hit (`PlayerPhysics.Tick`/`MoveWithCollision` now return a horizontal-collision
bool, fed back as `_horizontalCollision`), or — in **Survival** — hunger drops to `≤ 6` (`EntityPlayer.Hunger`,
mirrored from the stats packet; Creative is never gated). Sprinting passes `1.3×` to the walk branch (and the
faster water accel to the swim branch) and **widens the FOV** to `SprintFovScale` (1.15×), eased via
`PlayerController.FovScale` which `StateWorld` multiplies into the projection FOV (clamped ≤ 179°). Sprint
costs no extra server bookkeeping: the server already approximates exhaustion from the reported position delta
(sprinting just moves farther, see [entities.md](entities.md)).

The block-target raytrace uses the **eye** (`RenderPosition + EyeOffset`); `SendMove` ships the **feet**
position, so remote players (drawn by `EntityRenderer` as 0.6×1.8 boxes, offset up by half-height) line up.
**Remote entities are render-interpolated:** positions arrive at 20 tps, so `Entity.SetInterpTarget` (per
`EntityMove`) aims a lerp from the current visual position toward the new target, advanced per frame by
`WorldClient.UpdateEntityInterpolation`, and `Entity.RenderPosition` returns the lerp.

**Survival.** The 20 tps `Tick` runs player physics → `_integratedServer.Update()` (which runs the survival
sim, [entities.md](entities.md)) → `_network.Pump()` (handles fall/gamemode/respawn, broadcasts stats) →
`SendMove`. Each frame `StateWorld` mirrors the server's `GameMode` onto the local player (so the fly toggle is
gated to Creative — double-tap Space does nothing in Survival) and force-clears flight in Survival. The
**survival HUD** (`Client/GUI/SurvivalHud.cs`) draws hearts + hunger above the hotbar from the official
`icons.png` (placeholder bars without a resource pack), only in Survival. On death (`WorldClient.PlayerDead`,
from the stats packet) `StateWorld` opens the **`GuiDeathScreen`** overlay (Respawn / Title); singleplayer keeps
the integrated server ticking *while dead* (`simulate |= PlayerDead`) so the `RespawnRequest` is processed even
though the overlay unfocused the world. When the server clears the dead flag, `StateWorld` closes the overlay
and snaps the (position-authoritative) player to the spawn point. The **pause menu** carries a "Game Mode:
Survival/Creative" toggle button that sends `SetGameModeRequest`; the label follows the authoritative mode
(updated optimistically on click so it responds even while the singleplayer pause has frozen the server pump).
The Inventory key opens the **survival** inventory (`GuiInventory` — 2×2 crafting + 36 slots over
`container/inventory.png`) in survival and the creative item picker (`GuiCreativeInventory`) in creative.

**Block breaking.** Creative breaks instantly on left-click (unchanged). Survival is hold-to-mine
(`PlayerController.HandleBreaking`, per display frame): holding left-click accrues progress on the targeted
block at the Minecraft mining rate (`BreakSeconds`), and breaks it (the existing `SetBlock`-to-air request) when
full; the progress resets if the target changes or the button is released. The rate follows vanilla exactly: a
held tool's `Item.MiningSpeed` multiplier applies only when its `Item.ToolType` matches the block's
`Block.PreferredTool`, and the per-tick destroy progress is `speed / hardness / divider` where `divider` is
**30** with the correct tool (or for blocks that don't require one) and **100** otherwise — so mining stone by
hand takes the full 7.5 s, a wooden pickaxe ~1.1 s. A block with `RequiresCorrectTool` only reaches the ÷30 rate
when the matching tool's `Item.ToolTier` meets its `Block.RequiredToolTier`. Negative hardness (bedrock) is
unbreakable. The progressive crack overlay (`destroy_stage_0..9`) is drawn by `BlockBreakRenderer` (see
[rendering.md](rendering.md)).

**Tools.** `ItemTool` (VanillaPlugin) is a pickaxe/axe/shovel with a material's speed + tier (wood 2/0, stone
4/1, iron 6/2, gold 12/0, diamond 8/3); each material registers all three. Tools stack to 1 and have **no
durability** (see [known-issues.md](known-issues.md)). Blocks declare their preferred tool / tier / requires-tool
on `BlockBasic`'s ctor.

**HUD/GUI textures** come from the resource pack's modern (1.20.2+) **individual sprite** layout — the hotbar
(`hud/hotbar.png` + `hud/hotbar_selection.png`) and the survival HUD hearts/food (`hud/heart/*`, `hud/food_*`)
are separate PNGs, not the old monolithic `widgets.png`/`icons.png`; `GuiAssets` resolves each and callers fall
back to coloured placeholders when absent.

**Keybinds.** Movement/interaction keys are rebindable via `Keybinds` (`Client/Keybinds.cs`), a static
`GameAction → Key` (Silk `Key`) map persisted to `GamePaths.KeybindsFile` (`Keybinds.json`), loaded in
`Program.Main` alongside `GraphicsSettings`. `PlayerController` (and `StateWorld`/`ContainerScreen` for the
Inventory action) read the live binding via `Keybinds.IsDown` (held — `ClientResources.Input.IsKeyDown`) for
continuous movement, or `Keybinds.Matches(action, key)` against the `key` of an `OnKeyDown` event for discrete
presses, so a rebind takes effect immediately.
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
- **VSync** (Off/On/Adaptive) and **Fullscreen** (On/Off) — buttons. VSync's setter is the wgpu present mode
  (`GraphicsSettings.ApplyVSync → Renderer.SetVSync`, which reconfigures the surface); Fullscreen sets
  `ClientResources.Window.WindowState` (Silk).
- **Shadows** — a button cycling a **`ShadowQuality`** enum (Off/Low/Medium/High). It drives
  `WorldRenderer.ShadowDistance` (96/160/256) **and** `ShadowMapSize` (512/1024/2048); the shadow pass
  recreates its depth32float shadow-map `GpuTexture` when the size changes, and the shadow-resolve pass UBO's
  texel fields (`ShadowTexel` = world-space `2·radius/mapSize`, `ShadowMapTexel` = `1/mapSize`) track the
  resolution so the PCF disc scales with it. `GraphicsSettings.ShadowsEnabled` (≠ Off) gates the shadow-depth +
  resolve passes and the `ShadowsEnabled` flag in the shadow-resolve and composition pass UBOs.
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
  0–0.3 → `uMinLight` in `Composition.wgsl` (the `CompositionParams.MinLight` field), the unlit floor).
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

`Program.Main` calls `GraphicsSettings.Load()` before creating the window and seeds the `WindowOptions`
(fullscreen `WindowState`) from it; the saved VSync preference is pushed onto the surface in `OnLoad` once it
exists (`GraphicsSettings.ApplyVSync`). The rest are read live each frame. Numeric setters clamp to the
`Min*/Max*` consts. The dedicated server never touches `GraphicsSettings`.

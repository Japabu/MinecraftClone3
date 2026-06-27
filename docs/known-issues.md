# Known rough edges / deferred work

This list is for *open* work only — when an item is resolved, delete it or move its rationale into the
relevant permanent doc. Not a changelog.

- **WebGPU renderer — matches the GL renderer's image closely; a few overlay features aren't ported yet.**
  The GL→WebGPU (Silk.NET.WebGPU) migration runs on Metal and was diffed against the OpenGL `master` build
  with a seeded benchmark fly-through (10 frames/scene, same camera path): **the sky is pixel-identical** and
  terrain/water/shadows are within a few percent. The world is composited in display (gamma) space and the
  present pass just clamps to [0,1] — no tonemap curve and no gamma re-encode — to match the GL output exactly.
  Reverse-Z reconstruction (`PositionFromDepth` in Composition/ShadowResolve) negates ndc.y vs the uv basis and
  addresses the depth texel from uv (not the fragment's framebuffer position — wrong in the half-res shadow
  pass); the sky ray is rebuilt from two inverse-projection points (not a camera-relative single point). Open:
  - **`Frustum.Set` uses the GL `[-1,1]` near-plane formula** (`col3 + col2`) while WebGPU clip-z is `[0,1]`.
    The 4 side planes — which do the real chunk culling — are correct; near/far are approximate and, under the
    infinite-far projection, the far plane is a no-op (distance is bounded by the cull compute's `maxDistance`).
    Harmless for chunk culling; tighten if a near-camera clip artifact ever shows.
  - **Off-thread chunk upload is NOT done (migration level-up 3, deferred).** `ChunkRenderData` creation +
    `GpuBuffer` upload still run on the main thread; the WebGPU queue is thread-safe, so moving meshing→upload
    onto the mesh pool is the remaining performance level-up. `docs/threading.md` Invariants 1 & 2 still
    describe the main-thread model.
  - **GPU pass timing + GPU-culled draw counts read 0 (dev tooling, migration level-up M7 deferred).**
    `GpuTimers` is a no-op, so the F3 `gpu … ms` field and the profiler/benchmark `gpuMs`/`shadowMs`/`geomMs`/
    `compMs` columns are 0 (needs WebGPU timestamp-query `timestampWrites`, feature-gated). The F3 `chunks drawn`
    / `lod drawn` numerators are 0 because the GPU cull compute owns the post-cull count with no CPU readback
    (the CPU visible set is intentionally gone under GPU-driven culling — would need a count buffer map-back).
    The profiler CSV `gapMs` column is also 0 (the inter-frame gap timer wasn't carried onto the Silk loop).
    Gameplay/visuals are unaffected; `docs/profiling.md` documents the current 0 state.
  - **Deliberate, not defects** (don't "fix"): the `Gpu` static facade (global device access, single-threaded
    init) and the split where Core uses literal Silk types while Client/exe keep readability aliases
    (`Vector3`, `Matrix4`, …) — both documented in-code.

- **Benchmark screenshots omit the HUD.** `Screenshot` reads the HDR scene target, which is captured *before*
  `Renderer.EndFrame` tonemaps it to the surface and flushes the GUI (`GuiBatch`) over it — so the hotbar,
  crosshair, REC indicator and other overlays are on screen but not in the PNG. Capturing them needs the
  present pass to render into a readable LDR offscreen that the screenshot reads (a present-path change).
- **Nether is the "core, one-biome" slice.** Implemented: the dimension + generator (netherrack/lava/soul-sand/
  glowstone/quartz-ore), obsidian portals lit with flint & steel (`VanillaPortals`), 8:1 Overworld↔Nether
  travel with find-or-build destination portals, the multi-dimension server, and the sunless red-fog render
  mode. **Deferred / accepted:** only one biome (no soul-sand valley / crimson-warped forests, no fortresses,
  no nether mobs — ambient spawning is dimension-blind, so **Overworld animals can spawn in the Nether**);
  the portal renders the pack's real axis-oriented thin pane but is **not animated** (a static texture frame);
  **lava deals no damage** and is a pass-through fluid (no flow, no fire); the **current dimension is not
  persisted** — a player's position/look persists (`PlayerSerializer`) and is restored only when they log back
  into the *same* dimension they left, so a player who logged off in the Nether relogs at the **Overworld
  spawn** (the saved dimension key doesn't match) rather than back in the Nether. Restoring into a non-Overworld
  dimension at login would need the dimension-transfer flow run at join time. An Overworld return portal builds
  at the floor under the scaled coords, which may be far from where you left (no surface-match beyond a local
  floor scan / portal search radius of 16).
- **Dimension transfer briefly stalls the client.** `WorldClient.ResetForDimensionChange` parks the apply
  thread and tears the whole cached world down on the main thread (reusing the eviction paths), so the
  transfer frame can hitch by up to the apply-thread park latency (~50 ms) plus the teardown. One-time per
  portal trip; accepted.

- **Player physics is the "80%" walk model.** Implemented: gravity, jump, swept per-axis AABB collision, Ctrl
  sprint, walk/fly toggle, auto-step up `StepHeight` (0.6 = MC) ledges, non-cube collision (multi-box
  `GetCollisionBoxes`, used by stairs). **Not** implemented: sprint-jump forward boost, sneaking, per-block
  slipperiness (no ice/slime blocks exist), and **collision for creative flight** (flight is deliberately
  noclip). The exact-constant *ordering* (gravity-before-move vs after) may be a tick off MC and is tunable in
  `PlayerPhysics.Tick`.
- **Stairs are the "straight, 80%" stair.** `VanillaPlugin/Blocks/BlockStairs.cs` (`Vanilla:OakStairs`) uses
  the real `minecraft:block/oak_stairs` model; orientation (facing bits 0-1, top-half bit 2) is a mesh-time
  `GetModelTransform` rotation + matching multi-box L collision. Deferred/accepted: **no corner (inner/outer)
  variants**; the **raytrace/outline + targeting is the full cube** (highlight covers a bit of air over the
  low step); rotated faces keep their un-rotated normals/`cullface`; facing rides whole-chunk resends. The
  **yaw→facing mapping** and the top-half X-flip are the visual-tuning knobs in `BlockStairs`.
- **Walking into a not-yet-streamed chunk reads as air (could fall through an edge).** Collision uses
  `WorldBase.GetBlock`, which returns air for unloaded chunks. Bounded in practice: the join handshake
  pre-streams the spawn column and the client cache distance (240) stays well ahead of the server view
  distance (160), so terrain is normally resident before you reach it. A fast clip into ungenerated space
  could still drop the player; accepted.
- **Water is Tier B; Tier C is deferred.** Tier B is done (see [rendering.md](rendering.md) "Water surface").
  Residual edges: the reflection is the shared **`SkyColor`** skybox, so it tracks time of day but **doesn't
  reflect terrain or clouds** (no environment capture); there is **no refraction or depth-based absorption**
  (looking down shows the Tier-A tint over the bottom, Fresnel-mixed) — that's **Tier C** (a forward water
  pass after composition reading the opaque scene colour/depth). Water is also **not a fluid** — it doesn't
  flow, level, or fill; it's a static block placed by gen below sea level.
- **Distant-Horizons LOD is shipped (quality-first); a few refinements are deferred.** Default config: full
  per-block detail across the whole render distance (no within-RD LOD), then a huge Phase-2 horizon
  (`LodHorizonChunks` 64, max 96) of cheap LOD columns coarsening with distance (stride-4 → 8 → 16 rings,
  scaled by the **LOD Quality** option), with far-ring trees matched to real positions and a dithered
  cross-fade at the render-distance seam. See [rendering.md](rendering.md), [networking.md](networking.md),
  [worldgen.md](worldgen.md), [threading.md](threading.md). Residual edges:
  - **LOD throughput while moving — meshing is solved; gen/alloc is the remaining cost.** Reserved *LOD-first*
    mesh workers keep the visible horizon meshed even while cruising far out with a big render distance (peak
    visible-unmeshed ~3% in the far+heavy benchmark). What's still stride-2-driven: the store is stride-2
    (finest LOD = nearest ring), ~4× the column data of stride-4 → ~3× the allocation, **and** the server
    `FillLodRegion` does the full 4096-column noise even for a region that meshes away to stride-16 — so fast
    movement churns Gen2 GC / wasted gen. The clean fix is a **variable-resolution store**: generate near
    regions at stride-2 and far regions at stride-4/8/16, so the bulk of the horizon carries ¼–1/64 the data
    (needs per-region stride in the gen + packet + re-gen on band change). Until then a LOD-mesh queue drained
    **nearest-first** (it's a plain FIFO) would further insure the visible band against bursts.
  - **Trees coarsen out at the far rings.** A super-cell takes its tallest member (max-surface downsample), so
    a forest stays a canopy plateau but an isolated tree shrinks to a blob and may drop out at stride-8/16. The
    canopy is leaf-only (no trunk).
  - **Stride-ring seams.** Wide rings keep adjacent regions within one step, so cracks are bounded; per-cell
    skirts (to the neighbour super-cell or `LodFloorY`) hide most, but a hairline gap at a step boundary on a
    steep slope is possible. Greedy-merging skirt runs would tighten this (deferred).
  - **No LOD light BFS / single surface section.** Columns are sky-15 flat (no torches/AO at the horizon) and
    K=1 (one surface run), so overhangs/floating islands aren't represented; a 2-section run-list is the
    deferred fix. **No LOD disk persistence** (regenerated on revisit). **No LOD shadows.**
  - **Tall coastline skirts read as bright vertical walls** where water meets a steep drop (harmless, no holes).
- **Animated textures show frame 0 only.** Strips are sliced and all frames uploaded + retained
  (`BlockTextureManager.AnimatedTextures` with `frametime`), but nothing cycles them yet. The animator
  (advance the sampled layer by a remesh-free uniform/layer swap) is the deferred path. The `.mcmeta`
  `frames`/`width`/`height` reorder fields are ignored (only square top-to-bottom strips at default order are
  handled — covers water/lava/fire).
- **Biome height blends; surface *blocks* and climate selection don't.** Terrain height is bilinearly blended
  across borders (`HeightBlendSpacing` lattice). Residual edges: (1) **surface blocks snap** at the border
  (grass↔sand) — intended, matches MC; (2) **thin-biome skipping** — a biome strip narrower than
  `HeightBlendSpacing` may touch no lattice corner and not contribute to the blend (rare; mitigated by a
  smaller spacing); (3) **mountain-on-coast sand shelf (cosmetic)** — a high-bias land corner can lift an
  ocean-classified column's blended height above `SeaLevel`, showing a small sand shelf (no flooding/voids).
  Tune `HeightBlendSpacing` (16 steeper / 32 gentler). Ocean/Beach are height-derived overrides, so an
  ocean-*variant* plugin biome isn't selectable — supporting one needs a small extension.
- **Gen skips the light BFS, so some sky/light is approximate** (self-corrects on the first nearby edit): no
  lateral sky spill into cave mouths or under overhangs, dense canopy doesn't shadow the ground (leaves keep
  seeded sky 15), deep water dims by a simple per-block gradient not a flood. Caves carved below sea level are
  **air, not water** (no fluid fill).
- **Decoration determinism relies on the ±1-chunk margin + bounded feature reach.** A feature writing more
  than ~1 chunk from its origin column would clip at borders (keep tree/vein extents small). Decoration is
  recomputed per vertical chunk in the band (RNG is Y-independent so it's consistent, but a tall column of air
  chunks re-runs the attempts before clipping) — bounded by the local surface-height gate; a tighter per-step
  Y gate is a possible optimization. Ore/`SurfaceHeight` recompute inside the carver/features duplicates
  per-column noise — background cost, not yet memoized.
- **Single active dimension.** `WorldServer` binds one dimension (`Vanilla:Overworld`); multi-dimension travel
  and per-chunk dimension metadata in saves are deferred. The generator's column scratch assumes the single
  LoadThread writer (Invariant 5).
- **Resident-chunk growth from the vertical band.** `TerrainRadius` (10) × the full `MinChunkY..MaxChunkY`
  (9 chunks) is a much larger loaded set than the old thin surface slab; the `UnloadThread` still evicts idle
  chunks but the working set is bigger. Tune `TerrainRadius`/`ChunkLifetime` if memory matters.
- **Sun shadows are one low-res map capped at `ShadowDistance` (160).** A single map covers
  `[ShadowNear, ShadowDistance]` (= `RenderDistance`) and fades at the edge. Low-res is the deliberate default
  (soft shadows, `ShadowMapSize` 1024). Raising `ShadowMapSize` (sharper) or `ShadowDistance` (more coverage,
  coarser texels) trades one for the other; a much larger distance would want a warped map or cascades to keep
  near detail, but CSM was deliberately removed and isn't coming back. Bias is a scene/driver-dependent
  tradeoff (`NormalBias`/`DepthBias` in `ShadowResolve.wgsl`,
  `ShadowStrength`/`ShadowSoftness`/`ShadowCasterExtent` and the shadow-pipeline `depthBias`/`slopeScale` in
  `WorldRenderer`) — may need a pass per backend. **`ShadowMapSize` (C#) and the `ShadowTexel` constant
  (`ShadowResolve.wgsl`) must change together.**
- **The shadow depth pass is a fixed per-frame cost, except it's now skipped in caves.** It redraws all
  in-range opaque geometry from the sun's POV every frame regardless of window size, so it hits hardest at
  *low* framerate headroom, not specifically at fullscreen. **Gated on `_anyShadowReceiver`** (a visible
  sky-exposed chunk within `ShadowDistance`), so it's skipped deep in a cave; above ground it can't be
  skipped/cached because the sun moves every frame. Knobs: a shorter `ShadowDistance` or smaller
  `ShadowMapSize`; re-rendering the map every N frames with a slightly stale sun is deferred.
- **No texel snapping → mild shadow shimmer while the camera moves.** Because the sun moves every frame the
  projection is intentionally unsnapped (snapping would flicker). The cost is faint sub-texel edge crawl while
  walking/turning; PCF softens it and the sun's own crawl masks it. If the day cycle were paused or made very
  slow, re-introducing texel snapping would be worth it. `DayLengthSeconds` (1200, the Minecraft 20-minute day)
  sets how fast the sun moves.
- **Only opaque chunks cast sun shadows; entities cast/receive none.** Transparent geometry (water/glass) is
  excluded from the shadow pass (a solid black shadow from translucent material is wrong; a coloured shadow
  would need a transmittance pass). Remote players render "unlit" (`normal.w==1`), so they neither receive nor
  cast shadows. Both deferred.
- **Light copy-on-grow allocates on the light thread during initial lighting.** Each genuinely-new light value
  entering a chunk's light palette returns a new `PaletteStorage`, so the first torch flood over a chunk
  allocates O(distinct light values) small arrays on the `UpdateThread` (background, off the render thread).
  Subsequent re-lights reuse existing values in place. A deliberate trade for the resident-heap + clone win;
  if it ever matters, batch the writeback into one rebuilt light container per flood instead of per-block
  `SetLightLevel`.
- **`PaletteStorage.IndexOf`/`Set` is a linear palette scan.** Fine for block ids (few distinct values) but a
  chunk with a large light palette (smooth RGB near a torch) pays O(palette) per `Set` on the light thread. A
  reverse `value→index` lookup built per grown snapshot would make it O(1) — deferred, background cost.
- **Pathological all-distinct chunks store slightly more than dense.** A chunk with hundreds–thousands of
  distinct values grows `bitsPerEntry` toward 12 and the palette toward 4096, so worst case (~14 KB) exceeds
  the old 8 KB dense — but this never occurs in practice. No fallback-to-dense is implemented.
- **`PaletteStorage.Read` doesn't validate entries are `< paletteCount`.** A packed index `≥ count` (only from
  corrupt/buggy-server bytes) would index `_palette` out of range in `Get`, thrown later on the mesh/main
  thread. The disk path fail-safes (try/catch → regenerate) and the TCP decode is wrapped in the apply
  thread's try/catch, but a *post-decode* `Get` throw is not guarded. Left unfixed deliberately: it can't fire
  in normal operation, validating would branch the per-block `Get` hot path, and a genuine palette bug should
  surface loudly.
- **Meshing throughput is the chunk-fill cost — now parallelized, two amplifiers remain.** The mesh stage is a
  **worker pool** (`Environment.ProcessorCount-2`, ≥1) draining the shared queue (one chunk per worker via the
  `_meshPending` claim, read-only chunk access, GL on the main thread). Two amplifiers deferred: (1) a single
  edit (or each newly-applied chunk during streaming) **full-remeshes the chunk plus up to six face
  neighbours** — fixable by remeshing only the affected sub-region; (2) each remesh is tens of ms because of
  per-vertex smooth-lighting (~4 `GetBlockLightLevel` + `IsFullBlock` *per vertex* over the whole min..max
  box) — fixable by caching per-vertex brightness.
- **Chunk saves are per-chunk, not batched per region.** `UnloadThread`/`Unload()` call `SaveChunk` once per
  dirty chunk, each doing one 256 KB index rewrite. Batching a region's dirty chunks into a single index
  rewrite is deferred — marginal now that the index is small, and `SaveChunk` early-outs on `!NeedsSaving`.
- **Sky light is "gen-seed + simple BFS" — known edit-time limitations (accepted scope).** Gen seeds the sky
  container directly (open air = 15, water dims with depth, caves = 0), so untouched terrain is well lit
  without a flood. On edit, `UpdateSkyValues` floods sky like block light but with two simplifications: (1)
  **sky attenuates −1 in every direction including down**, so a cell reached only by downward *spread* dims
  with depth (a freshly-dug straight shaft does *not* dim — each dug cell re-seeds at 15 via `SkyExposed`'s
  straight-up scan; the dimming shows in caves/tunnels reached sideways, the desired dark-cave look); (2)
  **the equal-value removal ambiguity** — placing an opaque block to shadow a sky column won't cleanly go dark
  straight down, because a side-adjacent sky-15 cell back-fills it (15 − distance). The correct fix (a
  persistent per-column heightmap + undimmed-vertical special-case, Minecraft's approach) is deliberately not
  done. `SkyExposed` is capped at `SkyScanMaxHeight` (256).
- **All-air chunks above terrain aren't streamed, so the client falls back to sky 15 for unloaded chunks.** An
  all-air chunk is `IsEmpty` and never added to `LoadedChunks`/streamed; its seeded sky never reaches the
  client. `WorldClient.GetSkyLight` returns `LightLevel.SkyMax` for any unloaded chunk. Side effect: the
  bottom face of a block whose neighbour chunk is merely *not-yet-loaded* briefly samples 15 until that chunk
  streams in.
- `StateWorld` connects synchronously on the main thread; a far/unreachable MP host briefly blocks.
- `ClientSession.SentChunks` shrinks only on `ChunkRelease`/dirty resend, so a misbehaving or crashed client
  could leave stale entries until it disconnects. Bounded in practice by client `CacheDistance` eviction;
  would need a server-side cap/timeout for hardening.
- **No real player identity, so inventories aren't per-player in MP.** The client sends an empty name in the
  `Login` packet, so every player's inventory saves to `Players/player.dat` (see [inventory.md](inventory.md)).
  Singleplayer is fine; distinct MP inventories need actual player names plumbed through login. Inventory edits
  are also **unvalidated** (creative sandbox) — the server stores whatever `InventoryAction`/`HeldSlot` sends
  beyond a slot-range clamp, same trust model as placement.
- **Inventory is creative-only; crafting is client-trusted.** Items are first-class (`Item`/`ItemBlock`
  registry, standalone items), recipes are loaded from the pack's `data/` tree with tag resolution, and the
  3×3 crafting table has full vanilla slot interaction (pick/place/split/drag) — see [inventory.md](inventory.md).
  Accepted scope: crafting is computed **client-side** and the result mutations go up as ordinary (unvalidated)
  `InventoryAction`s — fine for a creative sandbox, exploitable in real MP. No survival pickup/drop, no recipe
  book; no survival item pickup/drop. (The apple is edible and mining tools work — but tools have no durability
  and broken blocks drop nothing; see the tool/survival limitations above.)
- **Right-clicking a crafting table or furnace always opens it.** `Block.OnActivated` returning true suppresses
  placement, so you can't place a block against their face, and there's no sneak-to-place override.
- **Furnaces are plain-furnace only; no XP, no blast furnace/smoker.** Smelting matches `minecraft:smelting`
  recipes with a hardcoded vanilla fuel table (see [inventory.md](inventory.md)); blasting/smoking recipes are
  ignored and those blocks aren't registered. No experience is granted on collecting output (the engine has no
  XP system). Container slot edits are client-trusted like inventory edits. If a furnace is broken while its
  screen is open the screen shows stale state until closed (its block data is gone server-side).
- **The blockstate parser only handles the `variants` form.** `BlockStateDefinition` reads
  `blockstates/<name>.json` variants (model + x/y rotation; weighted lists take the first). The `multipart`
  form (fences, redstone, walls) resolves to null → the block falls back to its single `Block.Model`. Add
  multipart support before relying on connected/multipart blocks.
- **Crafting-grid items are returned to the inventory on close; leftovers are lost.** If the inventory is full
  when a crafting screen closes, items still in the grid/cursor that don't fit are dropped (no world item
  entities exist). Accepted for the creative sandbox.
- **Entity models load from Bedrock geometry data, but the loader is a useful subset.** `BedrockModelLoader`
  reads the built-in mobs from `*.geo.json` (see [entities.md](entities.md)) and honors per-cube
  `uv`/`inflate` and X-axis bone rotation. **Not** interpreted (so an arbitrary Blockbench/community export may
  not load faithfully): bone `parent` hierarchies (our `EntityModel` is a flat part list — the renderer pivots
  each part independently, no parent transform), cube `mirror` (we author explicit per-box UVs instead), and the
  Y/Z bone-rotation sign conventions (only the X-axis quadruped pitch is exercised + verified). Supporting
  parented bones would mean giving the renderer a real bone matrix stack. Cube **`origin`/`size`/`uv` and the
  leg pivots are transcribed from the official Mojang Bedrock `.geo.json` and verified cube-for-cube**, so the
  built-in mobs' geometry matches vanilla exactly. The one un-applied detail is cube **`mirror`** (vanilla
  mirrors the right-side legs and one arm/leg of the humanoid): we render those faces un-mirrored, which is
  near-invisible on the near-symmetric limb textures. Applying it means swapping the left/right face regions
  and reversing U in `EntityRenderer.AddBox`.
- **Entity persistence runs on chunk-unload + shutdown, not the periodic autosave.** Entities save when their
  chunk unloads and on clean shutdown (see [entities.md](entities.md)), so quitting is lossless; a hard crash
  loses entity motion since the last unload (chunks/players are bounded by the 30 s autosave, entities aren't).
  An entity that wanders entirely out of every loaded chunk between unload cycles ticks unchecked until it
  void-despawns rather than being saved — bounded and rare. Adding an entity pass to the periodic autosave
  would need it to run on the tick thread (where `Entities` lives), not the unload thread.
- **Entities are stored/queried by linear scan — fine now, wants a spatial index eventually.** The server keeps
  a flat `WorldServer.Entities` `HashSet`; `FindEntity` (entity-targeted use), `EntityCreature.NearestPlayer`
  (every creature, every tick), and the client's `PlayerController.PickEntity` (every right-click) all scan the
  whole set O(n). Bounded today by the ambient-spawn soft cap (few entities), but a dense world (big herds,
  many dropped items) would make the per-tick `NearestPlayer` scan the hot one. The fix is a **uniform spatial
  hash** (entities bucketed by cell, updated on move) so AI/picking/lookup query only nearby cells; defer until
  entity counts justify it. Entity ids could also use a `Dictionary<int, Entity>` to make `FindEntity` O(1)
  cheaply, independent of the spatial work.
- **Sheep wool is white-only (no dye colours), and the sheared sheep doesn't regrow it.** The wool overlay
  renders from `sheep_wool.png` (untinted), so all sheep are white; vanilla tints the wool by the sheep's dye
  colour. And there's no grass-eating, so a sheared sheep stays bare. Both deferred — the wiring (a per-sheep
  `SheepData`, the overlay layer) is in place, so colour is a tint on the overlay draw + a colour field in
  `SheepData`, and regrow is a server-side timer flipping `Sheared` back.
- **Ender pearl can still strand you in a thick wall.** Throwing a pearl at a block teleports you to its impact
  point (`EntityProjectile` → `WorldServer.PendingTeleports` → `PlayerTeleportPacket`, see [entities.md](entities.md));
  when that point would embed the player, `TryResolveLanding` pushes back along the pearl's incoming path (then
  up/horizontals) to a player-clear spot, and refuses to teleport if it finds none. This handles the common
  head-on wall hit, but a dead-on hit into a **thick** wall (or a pocket where the back-off direction is also
  blocked) can still drop you inside the geometry rather than in front of it. A proper swept depenetration
  (resolve the player AABB out along the minimum-translation axis, multi-block) is the real fix; deferred.
- **Tools/weapons/armor — accepted limitations.** Mining tools (`ItemTool`), swords (`ItemSword`), and armor
  (`ItemArmor`) exist with Minecraft-exact speed/tier, attack damage, and defense points, but **none have
  durability** (they never wear out — `ItemStack.Metadata` is free to repurpose as a damage value later). There
  are also **no block drops** — a broken block vanishes, so survival can't gather resources to *craft* gear yet;
  gear is obtainable only from the creative item picker (mob loot *does* drop now, via `LootTable`). The block's
  `requires-tool` gate currently only throttles mining *speed* (÷100), not drops, because there is no drop system.
  Armor reduces only **mob melee** damage (the armor-reducible path); fall/drown/void bypass it, matching MC.
- **Survival MVP — accepted limitations.** Tool/hardness data is set with real Minecraft values only on
  `BlockBasic`; other block types (leaves, grass, torch, stairs, crafting table, **furnace** — its file is
  off-limits — glowstone, water) use the `1.5` hardness / no-preferred-tool defaults.
  The survival inventory (`GuiInventory`) now has armor slots (functional), but **no offhand or 3D player
  preview** (those regions of `inventory.png` are inert), and **armor isn't rendered on the player model**. The
  breaking crack overlay assumes the deferred G-buffer attachment layout (diffuse/normal/light).

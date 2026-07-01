# Known rough edges / deferred work

This list is for *open* work only — when an item is resolved, delete it or move its rationale into the
relevant permanent doc. Not a changelog.

- **WebGPU/Metal renderer — a few overlay features aren't done yet.**
  The world is composited in display (gamma) space and the present pass just clamps to [0,1] — no tonemap curve
  and no gamma re-encode.
  Reverse-Z reconstruction (`PositionFromDepth` in Composition/ShadowResolve) negates ndc.y vs the uv basis and
  addresses the depth texel from uv (not the fragment's framebuffer position — wrong in the half-res shadow
  pass); the sky ray is rebuilt from two inverse-projection points (not a camera-relative single point). Open:
  - **`Frustum.Set` uses the GL `[-1,1]` near-plane formula** (`col3 + col2`) while WebGPU clip-z is `[0,1]`.
    The 4 side planes — which do the real chunk culling — are correct; near/far are approximate and, under the
    infinite-far projection, the far plane is a no-op (distance is bounded by the cull compute's `maxDistance`).
    Harmless for chunk culling; tighten if a near-camera clip artifact ever shows.
  - **Off-thread chunk upload is not done.** `ChunkRenderData` creation + `GpuBuffer` upload run on the main
    thread; the WebGPU queue is thread-safe, so moving meshing→upload onto the mesh pool is the remaining
    performance level-up. `docs/threading.md` Invariants 1 & 2 describe the main-thread model.
  - **GPU pass timing + GPU-culled draw counts read 0.**
    `GpuTimers` is a no-op, so the F3 `gpu … ms` field and the profiler/benchmark `gpuMs`/`shadowMs`/`geomMs`/
    `compMs` columns are 0 (needs WebGPU timestamp-query `timestampWrites`, feature-gated). The F3 `chunks drawn`
    / `lod drawn` numerators are 0 because the GPU cull compute owns the post-cull count with no CPU readback
    (the CPU visible set is intentionally gone under GPU-driven culling — would need a count buffer map-back).
    The profiler CSV `gapMs` column is 0 (no inter-frame gap timer on the current loop).
    Gameplay/visuals are unaffected; `docs/profiling.md` documents the current 0 state.
  - **The block sampler has no anisotropy** — WebGPU forbids combining nearest-magnification (the crisp
    pixel-art look) with hardware anisotropy, so the block sampler uses trilinear mips with no aniso;
    grazing-angle distant terrain is slightly blurry. Intrinsic platform trade-off.
  - **Deliberate, not defects** (don't "fix"): the `Gpu` static facade (global device access, single-threaded
    init) and the split where Core uses literal Silk types while Client/exe keep readability aliases
    (`Vector3`, `Matrix4`, …) — both documented in-code.

- **Cutout foliage still lacks minification AA (faint edge shimmer at distance).** The old mip-transition
  darkening/"shells" is gone — `BlockMipChain` dilates each cutout's transparent holes so the black RGB under
  them stops bleeding into minified leaf edges at sample time (see [rendering.md](rendering.md)). What's left
  is ordinary aliasing of the cutout *edge* itself: the alpha test is a plain hard 0.5 cutoff, so with no
  multisampled target the silhouette is 1-px hard and distant canopy can shimmer in motion. Anti-aliasing it
  needs an AA pass the deferred renderer doesn't have: **hashed alpha testing** (Wyman/McGuire) denoised by
  **TAA**, **MSAA alpha-to-coverage** (needs a multisampled target — costly in deferred), or brute-force
  **SSAA**. Revisit when an AA/temporal pass lands.
- **Benchmark screenshots omit the HUD.** `Screenshot` reads the HDR scene target, which is captured *before*
  `Renderer.EndFrame` tonemaps it to the surface and flushes the GUI (`GuiBatch`) over it — so the hotbar,
  crosshair, REC indicator and other overlays are on screen but not in the PNG. Capturing them needs the
  present pass to render into a readable LDR offscreen that the screenshot reads (a present-path change).
- **Nether is the "core, one-biome" slice.** Implemented: the dimension + generator (netherrack/lava/soul-sand/
  glowstone/quartz-ore), obsidian portals lit with flint & steel (`VanillaPortals`), 8:1 Overworld↔Nether
  travel with find-or-build destination portals, the multi-dimension server, and the sunless red-fog render
  mode. **Deferred / accepted:** only one biome (no soul-sand valley / crimson-warped forests, no fortresses,
  and no nether-specific mobs — ambient spawning is dimension-gated, so the Nether simply has no ambient spawns
  until nether mobs exist); the portal renders the pack's real axis-oriented thin pane and animates (via the
  shared block-texture animator), and travel charges up over ~4 s in survival (near-instant in creative) with a
  thickening on-screen portal tint plus a nausea screen-warp; lava deals contact damage but is still a **pass-through fluid** (no flow,
  no fire aftereffect). A round trip reconnects to the original portal when one exists within the search box
  (`SearchExtent`, 16 horizontal × 48 vertical) — the transfer waits for that whole region to generate before
  searching, so it reuses the existing portal rather than building a duplicate against not-yet-loaded chunks;
  beyond the search box (a far first trip) it builds a fresh portal at the scaled floor, which may be far from
  where you left. Portal validity is
  checked on neighbour change (not polled), so a frame broken while the *portal's own* chunk is unloaded won't
  self-collapse until something next changes a block beside it once that chunk reloads — a narrow edge that
  self-corrects on the next interaction (the orphaned pane still works meanwhile).
- **Dimension transfer briefly stalls the client.** `WorldClient.ResetForDimensionChange` parks the apply
  thread and tears the whole cached world down on the main thread (reusing the eviction paths), so the
  transfer frame can hitch by up to the apply-thread wait interval (`_applySignal.WaitOne`) plus the teardown.
  One-time per portal trip; accepted.

- **Player physics is the "80%" walk model.** Implemented: gravity, jump, swept per-axis AABB collision, Ctrl
  sprint, walk/fly toggle, auto-step up `StepHeight` (0.6 = MC) ledges, non-cube collision (multi-box
  `GetCollisionBoxes`, used by stairs). **Not** implemented: sprint-jump forward boost, sneaking, per-block
  slipperiness (no ice/slime blocks exist), and **collision for creative flight** (flight is deliberately
  noclip). The move-then-apply-gravity ordering matches MC (see the comment in `PlayerPhysics.Tick`).
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
    mesh workers keep the visible horizon meshed even while cruising far out at a big render distance. What's
    still stride-2-driven: the store is stride-2
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
- **Animated textures play, but only the default frame order at one mip.** `BlockAnimator` cycles every strip
  (water/lava/fire/nether portal) by re-uploading the current frame into frame 0's atlas layer each tick — no
  shader/mesher change. Two accepted simplifications: only mip 0 is rewritten (lower mips keep frame 0's pixels,
  invisible because an animation's frame averages are near-identical), and the `.mcmeta` `frames` reorder list +
  `interpolate` are ignored (frames play top-to-bottom at `frametime`, so e.g. lava loops instead of
  ping-ponging) — both are pure polish on top of working animation.
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
- **Resident-chunk growth from the vertical band.** `TerrainRadius` (10) × the full `MinChunkY..MaxChunkY`
  (9 chunks) is a large resident set; the `UnloadThread` evicts idle chunks. Tune
  `TerrainRadius`/`ChunkLifetime` if memory matters.
- **Sun shadows are one low-res map capped at `ShadowDistance`.** A single map covers
  `[ShadowNear, ShadowDistance]` (= `RenderDistance`) and fades at the edge. `ShadowDistance` and `ShadowMapSize`
  are 3-way `GraphicsSettings.ShadowQuality` presets (Low 96/512, Medium 160/1024 default, High 256/2048); a
  low-res map is the deliberate default for soft shadows. Raising `ShadowMapSize` (sharper) or `ShadowDistance` (more coverage,
  coarser texels) trades one for the other; a much larger distance would want a warped map or cascades to keep
  near detail, but CSM was deliberately removed and isn't coming back. Bias is a scene/driver-dependent
  tradeoff (`NormalBias`/`DepthBias` in `ShadowResolve.wgsl`,
  `ShadowStrength`/`ShadowSoftness`/`ShadowCasterExtent` and the shadow-pipeline `depthBias`/`slopeScale` in
  `WorldRenderer`) — may need a pass per backend. **`ShadowMapSize` (C#) and the `ShadowTexel` constant
  (`ShadowResolve.wgsl`) must change together.**
- **The shadow depth pass is a fixed per-frame cost, skipped only in caves.** It redraws all
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
- **Only opaque chunks cast sun shadows; entities don't cast.** Transparent geometry (water/glass) is
  excluded from the shadow pass (a solid black shadow from translucent material is wrong; a coloured shadow
  would need a transmittance pass). Entities (including remote players) *receive* sun shadows + block light —
  they write `normal.w==0` so `ShadowResolve` lights them like terrain — but they don't *cast* (the shadow depth
  pass draws only the opaque chunk arena, not the entity pipeline). Entity casting and transmittance shadows for
  transparent material are deferred.
- **Light copy-on-grow allocates on the light thread during initial lighting.** Each genuinely-new light value
  entering a chunk's light palette returns a new `PaletteStorage`, so the first torch flood over a chunk
  allocates O(distinct light values) small arrays on the `UpdateThread` (background, off the render thread).
  Subsequent re-lights reuse existing values in place. A deliberate trade for the resident-heap + clone win;
  if it ever matters, batch the writeback into one rebuilt light container per flood instead of per-block
  `SetLightLevel`.
- **`PaletteStorage.IndexOf`/`Set` is a linear palette scan.** Fine for block ids (few distinct values) but a
  chunk with a large light palette (smooth RGB near a torch) pays O(palette) per `Set` on the light thread. A
  reverse `value→index` lookup built per grown snapshot would make it O(1) — deferred, background cost.
- **Pathological all-distinct chunks store slightly more than a dense array would.** A chunk with hundreds–
  thousands of distinct values grows `bitsPerEntry` toward 12 and the palette toward 4096 (~14 KB worst case),
  but this never occurs in practice. No fallback-to-dense is implemented.
- **`PaletteStorage.Read` doesn't validate entries are `< paletteCount`.** A packed index `≥ count` (only from
  corrupt/buggy-server bytes) would index `_palette` out of range in `Get`, thrown later on the mesh/main
  thread. The disk path fail-safes (try/catch → regenerate) and the TCP decode is wrapped in the apply
  thread's try/catch, but a *post-decode* `Get` throw is not guarded. Left unfixed deliberately: it can't fire
  in normal operation, validating would branch the per-block `Get` hot path, and a genuine palette bug should
  surface loudly.
- **Meshing throughput is the chunk-fill cost — parallelized across a worker pool, two amplifiers remain.** The mesh stage is a
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
- **Block-light spread BFS has a suspected edge-case at the empty-chunk guard.** `WorldServer.UpdateLightValues`
  bails on `LightChunkEmpty(nextNode)` during the spread; this guard is not fully verified and may drop or
  mis-propagate light across a freshly-emptied chunk boundary. Not yet diagnosed.
- **Smooth lighting is always on; there is no graphics-option toggle for it.** The mesher always applies the
  smooth-lighting / AO brightness curve; a per-player flat-lighting option is not wired up.
- `StateWorld` connects synchronously on the main thread; a far/unreachable MP host briefly blocks.
- `ClientSession.SentChunks` shrinks only on `ChunkRelease`/dirty resend, so a misbehaving or crashed client
  could leave stale entries until it disconnects. Bounded in practice by client `CacheDistance` eviction;
  would need a server-side cap/timeout for hardening.
- **Player identity has no in-game name UI, and inventory edits are unvalidated.** The login name
  (`PlayerSettings.Name` → `Login` packet → per-name `Players/<name>.dat`) defaults to `"Player"` and is only
  changeable by editing `PlayerSettings.json` — there is no name-entry screen, so two unconfigured clients
  collide. A connect-screen name field (or an OS-username default) is the follow-up. Inventory edits are also
  **unvalidated** (creative sandbox) — the server stores whatever `InventoryAction`/`HeldSlot` sends beyond a
  slot-range clamp, same trust model as placement.
- **Inventory is creative-only; crafting is client-trusted.** Items are first-class (`Item`/`ItemBlock`
  registry, standalone items), recipes are loaded from the pack's `data/` tree with tag resolution, and the
  3×3 crafting table has full vanilla slot interaction (pick/place/split/drag) — see [inventory.md](inventory.md).
  Accepted scope: crafting is computed **client-side** and the result mutations go up as ordinary (unvalidated)
  `InventoryAction`s — fine for a creative sandbox, exploitable in real MP. Survival item pickup/drop and block
  drops exist (`EntityItem` + `DropItemRequest`/`CollectItems`); still missing are a **recipe book** and
  **server-authoritative crafting**. (The apple is edible and mining tools work, but tools have no durability;
  see the tool/survival limitations above.)
- **Right-clicking a crafting table or furnace always opens it.** `Block.OnActivated` returning true suppresses
  placement, so you can't place a block against their face, and there's no sneak-to-place override.
- **Furnaces are plain-furnace only; no XP, no blast furnace/smoker.** Smelting matches `minecraft:smelting`
  recipes with a hardcoded vanilla fuel table (see [inventory.md](inventory.md)); blasting/smoking recipes are
  ignored and those blocks aren't registered. No experience is granted on collecting output (the engine has no
  XP system). Container slot edits are client-trusted like inventory edits. If a furnace is broken while its
  screen is open the screen shows stale state until closed (its block data is gone server-side).
- **Decoration blocks — accepted simplifications.** Plants (flowers/fern/grass tuft), slabs, ladders, fences,
  and walls are in (`VanillaPlugin/Blocks/Block{Plant,TintedPlant,Slab,Ladder,Fence,Wall,Connecting}.cs`),
  built on element-rotation cross models, the `variants`/`multipart` blockstate parser, a `Block.CanPlaceAt(world,
  pos, metadata)` placement gate (it receives the placement metadata because the block data isn't written until
  `OnPlaced`), `Block.ItemSpriteTexture` flat item icons, and `Block.IsClimbable` ladder physics in
  `PlayerPhysics`. Deferred/accepted edges:
  - **Plants don't pop off when their support is removed** — they float. `CanPlaceAt` blocks *placing* one off
    grass/dirt, but there's no neighbour-driven break (a one-shot scheduled tick would need the tick system to
    fire `OnServerTick` for non-`NeedsServerTick` blocks; making every plant tick every tick like a falling
    block was rejected as too costly for potentially world-carpeting grass).
  - **Grass/fern flat item icons are untinted** — `ItemSpriteTexture` blits the raw greyscale `short_grass`/`fern`
    sprite (the GUI/extrude path applies no biome tint), so their icon reads pale grey while the in-world block is
    green. Flowers/torch/ladder are full-colour and correct. Tinting the flat sprite is a deferred polish.
  - **Slabs don't merge into a double slab** — placing a slab against the matching opposite half makes a second
    separate slab, not a `type=double` (the place flow always targets the adjacent cell; merge needs a
    client-side placement-targeting special case). The `double` state renders/collides correctly but is
    currently only reachable via the blockstate, so it's dormant.
  - **Walls always show the centre post and only emit `low` side connections** — the vanilla post-cull (hide the
    post on a straight 2-connection run) and the `tall` height-under-overhang variant are simplified away;
    cosmetic only.
  - **No fence gates** (fences connect to fences + solid faces only), and the raytrace/outline for slabs,
    ladders, fences, and walls is the full cube (same accepted limitation as stairs).
  - **Multipart blockstates are supported** (`when`/`apply`, incl. `OR` and `low|tall` value lists; `AND` is
    ignored). The mesher emits every matching `apply` model; the inventory icon/viewmodel falls back to the
    block's single `Model` (the `*_inventory` model) since the icon world has no neighbours.
  - **`uvlock` is implemented for the y-rotation top/bottom case** (fence/wall side arms): the mesher rotates the
    UV *rectangle* of the up/down faces about its centre (`ChunkMesher.RotateUv`, direction from the
    `CreateRotationY(-y)` convention) so a rotated arm's top texture stays world-aligned. Rotating the rect (not
    just permuting the four corner UVs) is what keeps a non-square region (a 6×8 wall-arm top) from *stretching*
    when a 90/270° turn swaps the footprint to 8×6. **Not** done: uvlock on the four vertical side faces (invisible
    on the thin arms) and any x-rotation uvlock; those keep the model UVs.
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
  durability** (they never wear out — `ItemStack.Metadata` is free to repurpose as a damage value later).
  Broken blocks drop their item form and mob loot drops via `LootTable`, gated like vanilla: creative-mode
  breaks drop nothing, and a `RequiresCorrectTool` block only drops to a matching tool of sufficient tier
  (`ServerNetwork.DropsOnBreak`). Armor reduces only **mob melee** damage (the armor-reducible path);
  fall/drown/void bypass it, matching MC.
- **Survival MVP — accepted limitations.** Tool/hardness data is set with real Minecraft values only on
  `BlockBasic`; other block types (leaves, grass, torch, stairs, crafting table, **furnace** — its file is
  off-limits — glowstone, water) use the `1.5` hardness / no-preferred-tool defaults.
  The survival inventory (`GuiInventory`) now has armor slots (functional), but **no offhand or 3D player
  preview** (those regions of `inventory.png` are inert), and **armor isn't rendered on the player model**. The
  breaking crack overlay assumes the deferred G-buffer attachment layout (diffuse/normal/light).

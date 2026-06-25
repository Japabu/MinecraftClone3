# Known rough edges / deferred work

This list is for *open* work only — when an item is resolved, delete it or move its rationale into the
relevant permanent doc. Not a changelog.

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
  tradeoff (`NormalBias`/`DepthBias` in `ShadowResolve.fs`,
  `ShadowStrength`/`ShadowSoftness`/`ShadowCasterExtent`/`GL.PolygonOffset` in `WorldRenderer`) — may need a
  pass on Mesa. **`ShadowMapSize` (C#) and the `ShadowTexel` constant (`ShadowResolve.fs`) must change
  together.**
- **The shadow depth pass is a fixed per-frame cost, except it's now skipped in caves.** It redraws all
  in-range opaque geometry from the sun's POV every frame regardless of window size, so it hits hardest at
  *low* framerate headroom, not specifically at fullscreen. **Gated on `_anyShadowReceiver`** (a visible
  sky-exposed chunk within `ShadowDistance`), so it's skipped deep in a cave; above ground it can't be
  skipped/cached because the sun moves every frame. Knobs: a shorter `ShadowDistance` or smaller
  `ShadowMapSize`; re-rendering the map every N frames with a slightly stale sun is deferred.
- **No texel snapping → mild shadow shimmer while the camera moves.** Because the sun moves every frame the
  projection is intentionally unsnapped (snapping would flicker). The cost is faint sub-texel edge crawl while
  walking/turning; PCF softens it and the sun's own crawl masks it. If the day cycle were paused or made very
  slow, re-introducing texel snapping would be worth it. `DayLengthSeconds` (240) sets how fast the sun moves.
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
  book. Standalone items are inert (the apple isn't edible, no tools/durability).
- **Item ids aren't remapped from disk.** Like block ids, the `registry.bin` save/load path exists but is
  unwired, so item ids are assigned by registration order — stable only for a fixed plugin set. A changed
  plugin set shifts ids and a saved inventory would show wrong items (delete the world, per the no-back-compat
  rule).
- **Right-clicking a crafting table always opens it.** `Block.OnActivated` returning true suppresses placement,
  so you can't place a block against a crafting table's face, and there's no sneak-to-place override.
- **Crafting-grid items are returned to the inventory on close; leftovers are lost.** If the inventory is full
  when a crafting screen closes, items still in the grid/cursor that don't fit are dropped (no world item
  entities exist). Accepted for the creative sandbox.

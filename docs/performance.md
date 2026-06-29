# Performance notes — don't regress these

Why hot-path code is shaped the way it is — invariants not to regress.

- **The geometry pass is triangle / primitive-setup bound — NOT fill, bandwidth, draw-call, or overdraw
  bound.** The whole visible opaque set draws in a single GPU-driven multidraw, so draw-call count is a
  non-issue; overdraw is negligible (exposed-face meshing) and the fullscreen composition pass is cheap (fill
  is not the lever). `geomMs` scales linearly with **drawn-chunk count** (≈ triangle count), so the only lever
  is **fewer triangles** — which is what the Phase-2 flat-top + skirt LOD meshing of distant terrain buys (see
  [state-gameloop.md](state-gameloop.md) for the LOD options). **The shadow-resolve PCF pass is
  the exception — it is fill-bound**, so shorten `ShadowDistance` / `ShadowMapSize` there, not geometry.
- **Culling is GPU-driven; the CPU never builds an opaque visible set.** The shared `ChunkMeshArena` publishes
  one `ChunkMeta` per resident chunk (its world-space `MinCorner` + index range) into a storage buffer; each
  frame `ChunkCuller.Dispatch` runs the `Cull` compute shader to frustum/distance-test those AABBs and append
  a `DrawIndexedIndirect` command per visible chunk. The geometry and shadow passes then issue **one**
  `MultiDrawIndexedIndirectCount`. **Known Metal cost:** wgpu lacks `MultiDrawIndirectCount` on Metal
  (`Gpu.Features.MultiDrawIndirectCount` is false), so `ChunkCuller.Draw` falls back to a per-slot
  `DrawIndexedIndirect` loop over every resident slot — the cull still zeroes culled slots to no-op commands,
  but the loop is O(resident slots), not O(visible). The per-frame CPU scan (`ScanTransparentAndShadow`)
  iterates `WorldClient.RenderList` only to build the CPU-sorted transparent draw list (the GPU can't sort
  alpha) and to flag whether any sky-exposed chunk is in shadow range.
- **The renderer iterates a main-thread `List`, not the `RenderData` `ConcurrentDictionary`.** The transparent
  scan can't gate its work on player-chunk (the camera rotates), so its per-frame enumeration must be cheap.
  `WorldClient.RenderList` (a plain main-thread `List<ChunkRenderData>`) mirrors `RenderData`'s values —
  appended in `DrainRenderReady`, **swap-removed** in `UnloadChunk` via `ChunkRenderData.RenderListIndex`
  (O(1)) — and the transparent/shadow scan iterates it by index. `RenderData` stays a `ConcurrentDictionary`
  purely for by-position lookups. The profiler's `renderData` column reads `RenderList.Count` (a field)
  instead of `RenderData.Count` (which acquires all the dictionary's locks). The LOD horizon mirrors the same
  pattern (`LodRenderList`).
- **Opaque chunk meshes share one arena, not a buffer per chunk.** `ChunkMeshArena` suballocates every chunk's
  opaque vertices/indices out of one big set of GPU buffers via a coalescing first-fit `RangeAllocator`
  (main-thread bookkeeping); positions are baked world-space at mesh time, so no per-chunk model matrix is
  needed and the whole set is one multidraw. A remesh whose vertex/index counts match exactly reuses its
  sub-range in place (and keeps its `MetaSlot`, so the `Allocation`'s meta index stays valid); otherwise it
  frees and reallocates. Growth doubles the backing buffers on a transient command encoder submitted
  immediately (independent of the frame encoder). `FlushMeta` re-uploads only when the metadata changed and
  signals a cull bind-group rebuild (by handing back a new `MetaBuffer`) when the meta buffer grows.
- **The packed vertex is 32 bytes** (exact field layout in [rendering.md](rendering.md) / `MeshBuffer.cs`).
  Voxel normals are exactly the 6 axes (a lossless index); tint and light are 0..1 and the
  G-buffer stores them RGBA8 anyway (lossless at 8-bit). The 32-byte vertex keeps geometry-pass vertex
  bandwidth — the bottleneck at high render distance — and the mesh-thread allocation low; the vertex shader
  unpacks back to the same varyings, so the fragment shader is unchanged. The arena and the transparent VAO
  upload these as **five parallel vertex streams** (one buffer/attribute per slot, matching
  `WorldGeometry.wgsl`); the shadow pipeline binds only slot 0 (position).
- **The mesh upload is non-blocking (`ChunkRenderData.TryUpload`) — don't reintroduce a blocking upload.**
  `ChunkRenderData.Update()` (mesh thread) holds the opaque-buffer + transparent-VAO locks for the entire CPU
  remesh (tens of ms, per-vertex smooth-lighting), and a single edit remeshes the chunk plus up to six face
  neighbours, so a blocking upload taking those same locks would stall the render thread. `TryUpload` uses
  `Monitor.TryEnter`; on contention `WorldClient.RequeueUpload` defers the chunk. The upload loop is
  **time-budgeted** (`UploadBudgetMs`, ~4 ms/frame), not a fixed chunk count, because the mesh **pool**
  out-produces one frame's upload capacity. Failures are collected and requeued *after* the loop, so each
  position is dequeued at most once per frame.
- **WebGPU resource creation + queue submission stay on the main thread.** Render-data creation
  (`ChunkRenderData`) and the arena/VAO `QueueWrite` uploads run on the main thread, alongside the per-frame
  surface + encoder + `Queue.Submit`. The mesh pool does CPU meshing only (`ChunkRenderData.Update` builds the
  CPU `MeshBuffer`). The arena's `Upload` reuses an existing exact-fit sub-range to avoid an alloc/free churn,
  and writes each stream with a single `QueueWrite` straight off the backing `List` (`CollectionsMarshal.
  AsSpan`, no `ToArray`).
- **The transparent VAO is per-chunk and lazily allocated.** Translucent faces need an independent per-frame
  back-to-front sort, so each chunk keeps its own `SortedVertexArrayObject` (five vertex streams + an index
  buffer it rewrites on every camera move via `Sort`). Its `_faceInfos`/`_uploadedFaces`/`_sortedIndices` and
  the GPU buffers themselves are **allocated lazily on the first transparent face**, since most chunks are
  fully opaque — eager per-chunk backing arrays would be wasted resident memory on opaque chunks. `FaceInfo`
  is a **struct** (no per-face object); the sort
  rebuilds indices into a reused `List<uint>` and re-`QueueWrite`s the index buffer.
- **Meshing is allocation-free per face.** `ChunkMesher.AddFaceToVao` writes the four vertices straight into
  the CPU `MeshBuffer` and appends the six indices in place from the shared winding pattern
  (`MeshBuffer.AddFace(baseVertex, flipped, faceMiddle)`; per-face UVs cached on `BlockModel.FaceData`)
  instead of newing arrays per face. The CPU vertex `List<T>`s are recycled through `VaoBufferPool`
  (thread-safe `ConcurrentBag` per element type, pre-sized to a typical surface chunk's vertex count): `Add`
  rents on the first vertex (mesh thread), `Clear` returns after the GPU upload (main thread), so a remesh
  allocates no lists steady-state. The pool bounds retained buffers to the in-flight working set.
- **`WorldClient.MeshStepFor` keys LOD on horizontal (XZ) distance**, matching the horizontal LOD annulus +
  `EvictDistantLod`, so altitude never coarsens the horizon (flying up doesn't stair-step the ground).
- **Chunk storage is bit-packed paletted** (`PaletteStorage`; see [world-model.md](world-model.md)). Uniform
  chunks and single-value light/sky containers cost almost nothing, keeping the per-chunk clone and resident
  heap small. The flat `Chunk.Index(x,y,z) = (x*16+y)*16+z` ordering is the linear index into the packed
  `long[]` and defines the (de)serialize iteration order.
- **Light BFS reuses presized scratch + a chunk cache.** `WorldServer.UpdateLightValues` reuses
  `_lightSpreadQueue`/`_lightRemoveQueue`/`_lightLevelCache`/`_lightBlockCache`/`_lightChanged` instead of
  allocating queues + dictionaries per call (safe: `UpdateThread` is the sole caller). The dicts are pre-sized
  (8192) so the visited-node memo doesn't resize during a large flood.
  `_lightChanged` separates "visited" (memoised for lookup) from "actually changed" so the writeback
  dirties/enqueues only changed nodes (see the [networking.md](networking.md) resend-flooding note). The
  per-neighbour empty-chunk test goes through `_lightChunkCache` (memoised for one flood), not a fresh
  `LoadedChunks.ContainsKey`.
- **Light removal re-spread uses the neighbour's own level.** In the removal BFS, a neighbour whose light is
  `>=` the removed value belongs to a stronger source and is pushed to the spread queue enqueued with
  `nextNodeLightLevel[color]` (its actual level), not the removed `node.Value`. **Behavioural** — sanity-check
  place/break near a torch if touching this.
- **Server background threads reuse per-tick scratch.** `LoadThread` allocates nothing per tick (player
  snapshot, per-player candidate lists, dedup, round-robin merge output all reused; distance sort uses a
  cached closure-free `Comparison`; `ExtensionHelper.ZipMerge` fills a caller-owned list with a plain loop, no
  LINQ). `LoadChunk` resolves `Vanilla:Grass`/`Vanilla:Dirt` once. `UnloadThread` hoists `DateTime.Now`,
  reuses `_unloadScratch`, dedups atomically via `_chunksReadyToRemove.Add` under that set's own lock.
  `WorldServer.Update()` drains with `foreach` + `Clear()`, not LINQ. `UpdateThread` blocks on `_lightSignal`
  when idle instead of `Thread.Sleep(1)`.
- **Client per-frame allocations are reused, not re-newed.** `WorldRenderer` keeps one static `Frustum` and
  calls `Set(viewProjection)` each frame instead of newing a `Plane[6]`. `WorldClient.Update` reuses
  `_toUploadScratch` and **caps** the packet drain (`MaxPacketsPerTick` 64) and render-ready drain
  (`MaxRenderReadyPerTick` 256) so a burst can't process unbounded packets / create unbounded render-data in
  one frame. `StateEngine.Update` reuses `_overlaysToRemove` and runs the overlay pass as a reverse `for` loop
  with index access, not LINQ. Frustum culling (`Frustum.SpehereIntersection`) uses a plain loop, not LINQ,
  and `ServerNetwork.StreamChunks` tests interest against reused scratch lists instead of rebuilding a
  whole-world `HashSet<Vector3i>` per tick.
- **GUI text and the profiler don't allocate per frame.** `Font.MeasureWidth`/`DrawRun` decode codepoints
  through `NextCodepoint(text, ref i)` instead of the `Codepoints` `yield` iterator (which allocated an
  enumerator per call — every label is measured *and* drawn each frame); the iterator is kept only for
  one-time load paths. `Profiler.Record` (active under F10, but the maintainer profiles with it on) formats
  its CSV row into a reused `StringBuilder` via stack-buffer `TryFormat` (InvariantCulture) instead of
  `string.Join`, and uses `GC.GetTotalMemory`/`GetTotalAllocatedBytes`/`CollectionCount`, never the costly
  `GetGCMemoryInfo`.
- **Region index is KB-sized.** `RegionStore.ChunksInRegion = 32` → a `32³ × 8 B = 256 KB` flat index (two
  `int`s, pos+length, per slot). `Save` rewrites the index per saved blob and `Load` decompresses it per cache
  miss, so this size matters; the `MaxCachedIndexDatas` LRU (16) holds a few MB. The index write and each blob
  append stream through `GZipStream`, and the stored blob length comes from the append-stream `Position` delta.
  `WorldSerializer` just pairs two `RegionStore`s — chunk blocks (`.ri`/`.rd`) and chunk entities
  (`.rei`/`.red`). `ChunksInRegion` defines the on-disk region grid — changing it requires regenerating `World/`.
- **Singleplayer chunk streaming is serialize-, GZip-, and copy-free on the produce side.**
  `LoopbackConnection` passes the `Packet` object **by reference** (queues `Packet`, not `byte[]`) — safe
  because both endpoints are pumped sequentially on the client main thread and the server builds a fresh
  packet per `Send` it never reads back. **`ChunkDataPacket` compression/serialization is lazy — inside
  `Write`/`Read`, not `From`.** Over loopback `Write`/`Read` never run, and the carried chunk is cloned via
  `new Chunk(world, source)` — a paletted copy (`PaletteStorage.Clone`: small palette + packed `long[]`,
  race-tolerant) — **off the render thread entirely** (the apply thread). TCP is
  unchanged on the wire; `Read` copies the still-compressed bytes and the apply thread decompresses +
  deserializes. The `Chunk(CachedChunk)` ctor adopts the `CachedChunk`'s paletted storage by reference, so the
  server's `Update` drain does no chunk copying (the palette build happens on the `LoadThread`).
- **The client runs Server + Concurrent GC** (`MinecraftClone3.csproj`: `<ServerGarbageCollection>` +
  `<ConcurrentGarbageCollection>`). Server GC parallelizes the apply/mesh/server-background allocations across
  cores; keep it. The two csproj lines are the revert switch. (The dedicated server uses default GC — set the
  same there if its tick stalls under load.)
- **The all-loaded-chunks interest scans are gated on player-chunk change.** `ServerNetwork.StreamChunks` and
  `WorldClient.EvictDistantChunks` skip the whole-`LoadedChunks` `ConcurrentDictionary` scan unless the player
  crossed a chunk border (`StreamChunks` also rescans while a send backlog remains or the loaded-chunk count
  changes, via `ClientSession.StreamScanChunk`/`StreamScanLoadedCount` and `WorldClient._lastEvictChunk`).
  Standing still does **zero** per-frame chunk enumeration. Safe because chunks only ever stream in within
  `ViewDistance` (< `CacheDistance`).
- **The screenshot reads back the HDR scene target, not the swapchain.** `Screenshot` copies
  `Renderer.HdrScene` (created with `CopySrc`); the scene target is bottom-up (Tonemap samples it y-up via uv)
  so `DecodeHdr` flips rows to land the PNG top-down, and it's captured before tonemap/GUI flush so the HUD is
  omitted. The copy's `bytesPerRow` is rounded
  up to a multiple of 256 (a WebGPU requirement); the rgba16float scene is 8 bytes/texel.

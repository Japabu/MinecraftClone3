# Performance notes (implemented — don't regress these)

Why hot-path code looks the way it does, so a later change doesn't unknowingly undo a measured win. Settled,
not open work.

- **The geometry pass is triangle / primitive-setup bound — NOT fill, bandwidth, draw-call, or overdraw
  bound.** Every other suspect was ruled out by measurement: batching all opaque draws into one
  `glMultiDrawElementsBaseVertex` cut `renderMs` but left GPU time unchanged (Mesa non-indirect multidraw is a
  CPU loop); removing the cutout `discard` to enable early-Z moved `geomMs` ~5 % (overdraw is negligible —
  exposed-face meshing); the fullscreen composition pass is ~0.18 ms (fill is cheap). `geomMs` scales linearly
  with **drawn-chunk count** (≈ triangle count), so the only lever is **fewer triangles** — which is what the
  Phase-2 flat-top + skirt LOD meshing of distant terrain buys (`geomMs` ~4.8→1.2 ms, the big win; see
  [state-gameloop.md](state-gameloop.md) for the LOD options). **The shadow PCF pass is the exception — it is
  fill-bound and dominant on the integrated UHD 630**, so shorten `ShadowDistance` / `ShadowMapSize` there, not
  geometry.
- **`WorldClient.MeshStepFor` keys LOD on horizontal (XZ) distance**, matching the horizontal LOD annulus +
  `EvictDistantLod`, so altitude never coarsens the horizon (flying up doesn't stair-step the ground).
- **Chunk storage is bit-packed paletted** (`PaletteStorage`; see [world-model.md](world-model.md)). It
  replaced dense `ushort[4096]` + `LightLevel[4096]`, whose per-chunk clone dominated the render thread and
  whose resident heap drove a worsening GC stall. Paletted storage shrinks both ~10–50× (uniform chunks +
  single-value light/sky containers cost almost nothing). The flat `Chunk.Index(x,y,z) = (x*16+y)*16+z`
  ordering is the linear index into the packed `long[]` and defines the (de)serialize iteration order.
- **Light BFS reuses presized scratch + a chunk cache.** `WorldServer.UpdateLightValues` reuses
  `_lightSpreadQueue`/`_lightRemoveQueue`/`_lightLevelCache`/`_lightBlockCache`/`_lightChanged` instead of
  allocating queues + dictionaries per call (safe: `UpdateThread` is the sole caller). The dicts are pre-sized
  (8192) because resizing the visited-node memo was the entire light-thread cost as the sphere grew.
  `_lightChanged` separates "visited" (memoised for lookup) from "actually changed" so the writeback
  dirties/enqueues only changed nodes (see the [networking.md](networking.md) resend-flooding note). The
  per-neighbour empty-chunk test goes through `_lightChunkCache` (memoised for one flood), not a fresh
  `LoadedChunks.ContainsKey`.
- **Light removal re-spread uses the neighbour's own level.** In the removal BFS, a neighbour whose light is
  `>=` the removed value belongs to a stronger source and is pushed to the spread queue enqueued with
  `nextNodeLightLevel[color]` (its actual level), not the removed `node.Value` — the latter under-filled and
  left dark seams after repeated place/break near a light. **Behavioural**; sanity-check place/break near a
  torch if touching this.
- **Server background threads reuse per-tick scratch.** `LoadThread` allocates nothing per tick (player
  snapshot, per-player candidate lists, dedup, round-robin merge output all reused; distance sort uses a
  cached closure-free `Comparison`; `ExtensionHelper.ZipMerge` fills a caller-owned list with a plain loop, no
  LINQ). `LoadChunk` resolves `Vanilla:Grass`/`Vanilla:Dirt` once. `UnloadThread` hoists `DateTime.Now`,
  reuses `_unloadScratch`, dedups atomically via `_chunksReadyToRemove.Add` under that set's own lock.
  `WorldServer.Update()` drains with `foreach` + `Clear()`, not LINQ. `UpdateThread` blocks on `_lightSignal`
  when idle instead of `Thread.Sleep(1)`.
- **The mesh upload is non-blocking (`ChunkRenderData.TryUpload`) — don't reintroduce a blocking upload.**
  `ChunkRenderData.Update()` (mesh thread) holds the VAO locks for the entire CPU remesh (tens of ms,
  per-vertex smooth-lighting), and a single edit remeshes the chunk plus up to six face neighbours. A blocking
  `Upload()` taking those same locks stalled the render thread on every in-progress remesh — the per-edit
  frame spike. `TryUpload` uses `Monitor.TryEnter`; on contention `WorldClient.RequeueUpload` defers the
  chunk. The upload loop is **time-budgeted** (`UploadBudgetMs`, ~4 ms/frame), not a fixed chunk count,
  because the mesh **pool** produces chunks faster than one frame can upload (a fixed cap throttled the
  world-fill and ballooned the upload queue). Failures are collected and requeued *after* the loop, so each
  position is dequeued at most once per frame.
- **Chunk-mesh re-upload orphans the GPU buffer.** Re-specifying the *same* buffer with `glBufferData(data,
  StaticDraw)` while the GPU is still drawing from it forces an implicit CPU↔GPU sync (the CPU blocks until
  the draw finishes) — a per-edit spike under a deep frame queue, invisible to a CPU sampler. The shared
  `GlBuffer.UploadArray` keeps `StaticDraw` for a chunk's **first** upload and on a **re-upload orphans** the
  buffer (`glBufferData(target, size, IntPtr.Zero, DynamicDraw)` then `glBufferSubData`), so the driver hands
  back fresh storage and retires the old one once the in-flight draw completes (a "buffer rename"). The sorted
  VAO's index buffer stays `DynamicDraw` (`Sort()` rewrites it per frame). Orphaning is commonly-implemented,
  well-supported on Mesa + macOS GL; the fallback if a target fails to orphan is an explicit N-buffer ring
  (the 4.1 cap rules out GL 4.4 persistent-mapped buffers).
  [Khronos Buffer Object Streaming](https://wikis.khronos.org/opengl/Buffer_Object_Streaming).
- **VAO upload is zero-copy.** `VertexArrayObject`/`SortedVertexArrayObject`/`SpriteVertexArrayObject` upload
  straight from the backing `List<T>` via `ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(list))`
  (synchronous copy during the call) instead of `list.ToArray()`. Frustum culling
  (`Frustum.SpehereIntersection`) uses a plain loop, not LINQ, and `ServerNetwork.StreamChunks` tests interest
  against reused scratch lists instead of rebuilding a whole-world `HashSet<Vector3i>` per tick.
- **Meshing is allocation-free per face.** `ChunkMesher.AddFaceToVao` writes the four vertices straight to the
  VAO and appends the six indices in place via `VertexArrayObject.AddFace(baseVertex, flipped, faceMiddle)`
  (winding patterns on the VAO; per-face UVs cached on `BlockModel.FaceData`) instead of newing arrays per
  face. `SortedVertexArrayObject.FaceInfo` is a **struct** (no per-face object); its per-frame transparency
  sort rebuilds indices into a reused `List<uint>` uploaded via `BufferSubData`+`AsSpan`. Its
  `_faceInfos`/`_sortedIndices` lists are **allocated lazily on the first transparent face**, not eagerly at
  capacity 1024 — most chunks are fully opaque, so eager lists were KBs of resident empty backing arrays per
  chunk. The CPU vertex `List<T>`s are recycled through `VaoBufferPool` (thread-safe `ConcurrentBag` per
  element type): `Add` rents on the first vertex (mesh thread), `Clear` returns after the GPU upload (main
  thread), so a remesh allocates no lists steady-state. (Chosen over a per-VAO `Reset()` that keeps capacity:
  that would pin every loaded chunk's CPU mesh; the pool bounds retained buffers to the in-flight working
  set.) Non-meshing VAOs that build once and never `Clear` keep their rented lists for their lifetime.
- **Client per-frame allocations are reused, not re-newed.** `WorldRenderer` keeps one static `Frustum` and
  calls `Set(viewProjection)` each frame instead of newing a `Plane[6]`. `WorldClient.Update` reuses
  `_toUploadScratch` and **caps** the packet drain (`MaxPacketsPerTick` 64) and render-ready drain
  (`MaxRenderReadyPerTick` 256) so a burst can't process unbounded packets / create unbounded GL render-data
  in one frame. `StateEngine.Update` reuses `_overlaysToRemove` and runs the overlay pass as a reverse `for`
  loop with index access, not LINQ.
- **GUI text and the profiler don't allocate per frame.** `Font.MeasureWidth`/`DrawRun` decode codepoints
  through `NextCodepoint(text, ref i)` instead of the `Codepoints` `yield` iterator (which allocated an
  enumerator per call — every label is measured *and* drawn each frame); the iterator is kept only for
  one-time load paths. `Profiler.Record` (active under F10, but the maintainer profiles with it on) formats
  its CSV row into a reused `StringBuilder` via stack-buffer `TryFormat` (InvariantCulture) instead of
  `string.Join`, and uses `GC.GetTotalMemory`/`GetTotalAllocatedBytes`/`CollectionCount`, never the costly
  `GetGCMemoryInfo`.
- **Region index is KB-sized.** `WorldSerializer.ChunksInRegion = 32` → a `32³ × 8 B = 256 KB` flat index.
  `SaveChunk` rewrites the index per saved chunk and `LoadChunk` decompresses it per cache miss, so this size
  matters; the `MaxCachedIndexDatas` LRU (16) holds a few MB. Both the index write and each chunk append
  stream straight through `GZipStream` to the file (no intermediate `byte[]`); the chunk's stored length comes
  from the append-stream `Position` delta. `ChunksInRegion` defines the on-disk region grid — changing it
  requires regenerating `World/`.
- **Singleplayer chunk streaming is serialize-, GZip-, and copy-free on the produce side.**
  `LoopbackConnection` passes the `Packet` object **by reference** (queues `Packet`, not `byte[]`) — safe
  because both endpoints are pumped sequentially on the client main thread and the server builds a fresh
  packet per `Send` it never reads back. **`ChunkDataPacket` compression/serialization is lazy — inside
  `Write`/`Read`, not `From`.** Over loopback `Write`/`Read` never run, and the carried chunk is cloned via
  `new Chunk(world, source)` — a paletted copy (`PaletteStorage.Clone`: small palette + packed `long[]`,
  race-tolerant), not a dense `Array.Copy` — **off the render thread entirely** (the apply thread). TCP is
  unchanged on the wire; `Read` copies the still-compressed bytes and the apply thread decompresses +
  deserializes. The `Chunk(CachedChunk)` ctor adopts the `CachedChunk`'s paletted storage by reference, so the
  server's `Update` drain does no chunk copying (the palette build happens on the `LoadThread`).
- **The client runs Server + Concurrent GC** (`MinecraftClone3.csproj`: `<ServerGarbageCollection>` +
  `<ConcurrentGarbageCollection>`). Paletted storage + off-thread decode cut most of the GC pressure this
  fought, but Server GC still parallelizes the apply/mesh/server background allocations across cores, so keep
  it. The two csproj lines are the revert switch. (The dedicated server uses default GC — set the same there
  if its tick stalls under load.)
- **The all-loaded-chunks interest scans are gated on player-chunk change.** `ServerNetwork.StreamChunks` and
  `WorldClient.EvictDistantChunks` used to enumerate the whole `LoadedChunks` `ConcurrentDictionary` every
  frame (its `GetEnumerator` dominated CPU and spiked `updateMs` when it raced the apply thread). Now each
  skips the scan unless the player crossed a chunk border (`StreamChunks` also rescans while a send backlog
  remains and when the loaded-chunk count changes, via `ClientSession.StreamScanChunk/StreamScanLoadedCount`
  and `WorldClient._lastEvictChunk`). Standing still does **zero** per-frame chunk enumeration. Safe because
  chunks only ever stream in within `ViewDistance` (< `CacheDistance`).
- **The renderer iterates a main-thread `List`, not the `RenderData` `ConcurrentDictionary`.**
  `DrawGeometryFramebuffer` can't gate its frustum-cull on player-chunk (the camera rotates), so its per-frame
  `RenderData` enumeration (an O(bucket-table) walk + heap-allocated enumerator) dominated the render thread
  once the rendered set was large. Now `WorldClient.RenderList` (a plain main-thread `List<ChunkRenderData>`)
  mirrors `RenderData`'s values — appended in `DrainRenderReady`, **swap-removed** in `UnloadChunk` via
  `ChunkRenderData.RenderListIndex` (O(1)) — and `BuildVisibleSet` + the shadow caster cull iterate it by
  index. `RenderData` stays a `ConcurrentDictionary` purely for by-position lookups. The profiler's
  `renderData` column reads `RenderList.Count` (a field) instead of `RenderData.Count` (which acquires all the
  dictionary's locks).

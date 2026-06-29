# Threading model  ⚠️ the load-bearing invariant

```
 WorldServer (background, started in ctor):
   LoadThread    disk load else _generator.Generate over the full vertical band around each player in
                 PlayerEntities; fills _chunksReadyToAdd. Sole writer of generated chunks.
   UnloadThread  saves + evicts chunks idle > 30s; fills _chunksReadyToRemove
   UpdateThread  drains _queuedLightUpdates → UpdateLightValues (RGB BFS flood) then UpdateSkyValues
                 (sky BFS flood, same edited cell) per edit; blocks on _lightSignal (AutoResetEvent set by
                 SetBlock/SetBlockData) when idle, waking on a 100 ms timeout to observe _unloaded. Sole
                 writer of both the light and sky containers (post-publish)
   LodThread     (BelowNormal) fills _lodStore with cheap surface-only LOD columns + tree canopies
                 (GetLodColumn + DecorateLodRegion) in the ring beyond TerrainRadius out to LodRadius.
                 Sole writer of _lodStore; reads LoadedChunks/PlayerEntities; writes NOTHING to
                 chunks/light/dirty. Dormant until LodRadius > TerrainRadius (see [rendering.md](rendering.md))
   WorldServer.Update()  (caller's thread) drains add/remove into LoadedChunks; runs entity updates

 WorldClient:
   ApplyThread   the SOLE writer of chunk contents: drains _applyQueue in packet order → decodes streamed
                 chunks (SP: clone the carried Chunk; MP: decompress + deserialize) and applies BlockChanges
                 deltas in place → publishes to LoadedChunks → hands positions to the main thread via
                 _renderReady. NO GPU. Sleeps on _applySignal when idle. ALSO the sole client writer of
                 LodStore (decodes LodColumnData off the same ordered queue → _lodRenderReady).
   MeshThread    a POOL of workers (Environment.ProcessorCount-2, ≥1), each draining the shared mesh queues →
                 ChunkRenderData.Update()  (CPU vertex lists only, NO GPU; holds the mesh-buffer locks for the
                 whole remesh, so the main-thread upload must not block on them). It parallelizes because the
                 _meshPending claim (remove-under-_meshLock) gives each *queued* chunk to exactly ONE worker,
                 workers only READ chunk storage (Invariant 5), and GPU work stays on the main thread (Invariant 1).
                 Each worker also computes its chunk's SkyExposed flag (plain-field idempotent write, read
                 lock-free by the shadow gate — benign torn read self-corrects next remesh).
                 **Subtlety:** the _meshPending claim is per *queue-epoch*, not per *instance* — if QueueMesh
                 re-enqueues a position (an edit/light delta) while a worker is mid-Update on it, a second
                 worker can Update the SAME ChunkRenderData concurrently. Benign, NOT a bug: both calls
                 serialize on lock(_opaque)+lock(_transparentVao), each is a complete self-contained remesh (own
                 pooled lists, read-only storage), and TryUpload's Monitor.TryEnter never observes a half-built
                 mesh — the only cost is a redundant remesh (cannot fire during the edit-free load burst).
                 A small fraction of the pool is reserved *LOD-first* (lodWorkers = min(max(1, workers/4),
                 workers-1)); the rest are chunk-first, and each kind falls through to the other queue when
                 its own is empty. The reserved LOD workers keep the LOD mesh queue ~0 even under sustained
                 movement, when the chunk-first workers would otherwise stay saturated (see [rendering.md](rendering.md)).
   Update()      (MAIN thread) pumps packets (routing ChunkData/BlockChanges to _applyQueue, handling
                 entity/login inline), DrainRenderReady → creates ChunkRenderData (GPU) + queues meshing,
                 TryUploads meshed chunks (GPU buffer writes, non-blocking, time-budgeted per frame), evicts, disposes
   RenderWorld() (MAIN thread) cull-compute dispatches GPU-cull opaque + LOD + shadow chunks;
                 ScanTransparentAndShadow gathers only the CPU-sorted transparent list + shadow-receiver
                 flag, then the shadow + geometry + composition passes run

 Client game loop (MinecraftClone3/Program.cs, display rate ~120 Hz, MAIN thread):
   OnUpdateFrame → StateEngine.Update() → StateWorld.Update():
       per FRAME:  PlayerController.UpdateFrame (look/break/place/camera), world.Update() (GPU + packet pump + evict)
       per TICK (fixed 20 tps accumulator, 0..N times per frame) → StateWorld.Tick():
           PlayerController.Tick (one physics step)   // singleplayer + multiplayer
           integratedServer.Update();  network.Pump();  // singleplayer only (advances WorldServer.TickCount)
           world.SendMove(player);
       then ApplyInterpolation(alpha) renders the 20 tps motion smooth at the frame rate
   OnRenderFrame → StateEngine.Render() → WorldRenderer.RenderWorld(worldClient, projection)
```

**The whole simulation runs at a fixed 20 tps; input/look/camera/render run every display frame.**
`StateWorld` owns a real-time accumulator (`_simTimer`/`_simAccumulator`): each `Update` it adds the elapsed
frame time (clamped to `MaxFrameTime`) and runs `Tick()` while ≥ `PlayerPhysics.TickSeconds` (capped at
`MaxCatchUpTicks` so a stall can't spiral). A `Tick` is one player physics step, the integrated
`WorldServer.Update()` (which increments `TickCount` — the authoritative world clock, also 20 tps on the
dedicated server), `ServerNetwork.Pump()`, and the client `SendMove`. Between ticks the player position is
**render-interpolated** (`PlayerController.ApplyInterpolation(alpha)`, `alpha = accumulator / TickSeconds`),
so motion is smooth at the display rate. SP freezes the accumulator while paused (unfocused); MP keeps
ticking the remote server. `UpdateFrequency` stays at the display rate — only the sim cadence is fixed.

**The client chunk pipeline is split across three threads so the render thread does only GPU work + reads:** the
apply thread decodes/mutates chunk storage, the mesh thread builds vertex lists, the main thread does the GPU
work (render-data creation, upload) plus eviction. ChunkData and BlockChanges for the same chunk go through **one
ordered `_applyQueue`** so a delta can never race ahead of the chunk it targets.

## Invariants

**Invariant 1 — WebGPU resource creation + queue submission only on the main thread.** Uploading a mesh is
the WebGPU work: `TryUpload` writes (and grows) the shared `ChunkMeshArena`'s GPU buffers and lazily creates +
writes the transparent VAO's GPU buffers (the `ChunkRenderData` ctor itself allocates nothing on the GPU).
Therefore `ChunkRenderData`s are created **and** uploaded in
`WorldClient.DrainRenderReady` (main thread), **not** on the apply thread; the mesh thread only does CPU
meshing (`ChunkRenderData.Update`, vertex lists only); `Upload`/`Draw`/`Dispose` and the per-frame surface +
command encoder + `Queue.Submit` happen on the main thread. (wgpu-native's queue + buffer writes are
thread-safe, so moving chunk *upload* off the main thread is a planned level-up — see
[known-issues.md](known-issues.md) — but the current model keeps upload main-thread.) Never move WebGPU work
off the main thread.

**Invariant 2 — `ChunkRenderData.TryUpload()` is gated on `Updated` and is non-blocking.** The mesh thread
may enqueue the same chunk for upload more than once; `TryUpload` consumes+clears the vertex lists, so it
must run at most once per remesh — the `Updated` flag gates it (a second call is a no-op). Don't remove it.
**`TryUpload` must also never block:** `ChunkRenderData.Update()`
(mesh thread) holds the `_opaque`/`_transparentVao` locks for the *entire* CPU remesh (per-vertex
smooth-lighting makes it long), and a single edit remeshes the chunk **plus up to six face neighbours**. `TryUpload` uses
`Monitor.TryEnter`; if the mesh thread holds the lock it returns `false` and `WorldClient` re-queues that
chunk for a later frame instead of the render thread stalling on the lock for the whole remesh. The render
path (`Draw`/`Sort`/`DrawTransparent`) takes **no** mesh-buffer lock, so rendering is never blocked by meshing;
only the upload handoff is decoupled (via `TryEnter` + re-queue).

**Invariant 3 — shared collections.** `LoadedChunks` is a `ConcurrentDictionary` (client: apply thread adds,
main thread removes-on-evict, both + mesh thread read). `WorldClient.RenderData` is a `ConcurrentDictionary`
created/removed **only on the main thread** (`DrainRenderReady`/`UnloadChunk`); the mesh thread only reads it
(lock-free `TryGetValue`). The renderer does **not** enumerate `RenderData` each frame — it iterates
`WorldClient.RenderList`, a plain main-thread `List<ChunkRenderData>` mirroring `RenderData`'s values, kept
in sync O(1) (add in `DrainRenderReady`, swap-remove in `UnloadChunk` via `ChunkRenderData.RenderListIndex`).
`WorldServer.PlayerEntities` is mutated only via `AddPlayer`/`RemovePlayer` (locked) and the `LoadThread`
snapshots it under the same lock. `DirtyChunks` is a `ConcurrentDictionary` used as a set.
`ServerNetwork._sessions` is touched only on the tick thread (the accept thread enqueues to a concurrent
`_pending`).

**Invariant 4 — block/item-id agreement (within a session).** Numeric block/item ids are assigned in
**deterministic plugin order** at load (`PluginManager` sorts by plugin id), so the client and server agree on
the wire as long as they load the same `Plugins/`. The ids are session-local only: disk and the TCP
chunk/inventory payloads carry the stable registry **name**, remapped on read, so worlds/inventories survive
plugin churn and never depend on cross-run id stability (see [world-model.md](world-model.md)).

**Invariant 5 — per-`PaletteStorage`-container single writer + copy-on-grow.** Each `PaletteStorage` container
(block ids / light / sky) has exactly one writer thread; a published storage's palette and bit-width are
immutable, and growth publishes a new storage via a `volatile` field, so readers are lock-free. Never add a
second writer (see the [world-model.md](world-model.md) copy-on-grow rule).

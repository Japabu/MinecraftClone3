# CLAUDE.md

> **⚠️ KEEP THIS FILE UP TO DATE.** This is the single source of truth for how the project fits
> together. Whenever you change architecture, threading, the wire protocol, the build/run flow, an
> invariant, or a convention, **update the relevant section in the same change** — a stale diagram is
> worse than none. If a task proves something here is wrong, fix it here too. **Prune as you go:** when a
> "Known rough edges / deferred work" item is resolved, delete it or move its rationale to a permanent
> section (e.g. "Performance notes") — that list is for *open* work only, not a changelog of past fixes.
> Treat editing this file as part of "done," not an afterthought.

A from-scratch Minecraft-like voxel engine in C# on OpenTK (OpenGL). Custom deferred renderer, plugin
system, chunked world with RGB light propagation, and **client/server multiplayer** (singleplayer runs an
in-process server over a loopback connection; multiplayer connects to a dedicated server over TCP).

---

## Solution layout

```
MinecraftClone3.sln
├── MinecraftClone3API      Shared library — ALL engine logic lives here.
│   ├── Blocks/             WorldBase, WorldServer, Chunk (storage), CachedChunk, Block, LightLevel
│   ├── Client/             Client-only code (needs a GL context)
│   │   ├── Blocks/         WorldClient (client world replica)
│   │   ├── Graphics/       WorldRenderer, ChunkRenderData, EntityRenderer, BoundingBoxRenderer, VAOs, Camera
│   │   ├── GUI/            GuiBase, GuiButton, widgets
│   │   └── StateSystem/    StateEngine, StateBase, GuiBase
│   ├── Entities/           Entity, EntityPlayer, PlayerController
│   ├── IO/                 GamePaths, FileSystem*, ResourceReader, CommonResources, plugin file systems
│   ├── Networking/         IConnection, Packet(s), Loopback/Tcp connections, ServerNetwork, ClientSession
│   ├── Plugins/            PluginManager, IPlugin, PluginContext
│   └── Util/               GameRegistry, BlockRegistry, ChunkMesher, WorldSerializer, CompressionHelper, I18N
├── MinecraftClone3         Client executable (OpenTK GameWindow, 120 Hz). Owns Program + States/.
├── MinecraftClone3Server   Dedicated headless server executable (no GL).
└── VanillaPlugin           Content plugin: the actual blocks (Stone, Dirt, Grass, Torch, ...).
```

`MinecraftClone3` and `MinecraftClone3Server` are thin shells; nearly everything is in the API library.
Target framework **net10.0**. `<Nullable>` and `<ImplicitUsings>` are **disabled** — write explicit
`using`s and don't rely on nullable annotations.

---

## Build & run

```bash
dotnet build MinecraftClone3.sln -c Debug          # build everything
dotnet run --project MinecraftClone3 -c Debug       # run the client (needs a DISPLAY / GL context)
dotnet run --project MinecraftClone3Server -c Debug # run the dedicated server (headless, Ctrl-C to save+stop)
```

The server listens on **127.0.0.1:25565** (`ServerNetwork.DefaultPort`); the client's multiplayer button
connects there (`StateWorld.ServerAddress`). World saves live in `~/.local/share/MinecraftClone3/World`
(see `GamePaths`).

---

## Core architecture: one client path, two server transports

Singleplayer and multiplayer share **one** client code path. Singleplayer just runs the server in-process
and talks to it over an in-memory loopback connection; multiplayer swaps the loopback for a TCP socket.

```
 SINGLEPLAYER (all in one process)
 ┌───────────────────────── client process ─────────────────────────┐
 │  WorldClient ──IConnection── LoopbackConnection ──IConnection──┐   │
 │   (replica)    (client side)   (in-mem queues)    (server side)│   │
 │                                                    ServerNetwork   │
 │                                                        │           │
 │                                                    WorldServer     │
 └───────────────────────────────────────────────────────────────────┘

 MULTIPLAYER
 ┌──── client process ────┐                    ┌──── server process ────┐
 │ WorldClient ─TcpConn ──┼──── TCP socket ────┼─ ServerNetwork         │
 │  (replica)             │  length-prefixed   │      │                 │
 │                        │   binary frames    │  WorldServer           │
 └────────────────────────┘                    └─────────────────────────┘
```

- **`WorldServer`** (`Blocks/WorldServer.cs`): the authority. Block storage, terrain gen, RGB light
  propagation, save/load, entity simulation. **No meshing, no GL** — it can run fully headless.
- **`WorldClient`** (`Client/Blocks/WorldClient.cs`): the client replica. Holds chunks streamed from the
  server, **caches them and owns their eviction** (drops a chunk past `CacheDistance`, then sends a
  `ChunkRelease`), meshes them, renders them, holds remote entities. **No terrain gen, no disk, no lighting.**
- **`ServerNetwork`** (`Networking/ServerNetwork.cs`): per-client sessions, interest-based chunk streaming,
  dirty-chunk resends, entity relay, the TCP listener.
- **Authority:** server owns blocks + light. Position is **client-authoritative** (there is no server-side
  physics; `Entity.Move` is a direct position write). The client *requests* edits; the server applies and
  broadcasts the result.
- **Chunk lifetime is client-owned (see below).** The server streams a chunk once and keeps it in the
  session's `SentChunks` until the chunk is dirtied or the **client** releases it; the server never tells a
  client to unload. A client that walks away and back re-renders from its own cache — zero bytes on the wire.

---

## World & chunk model: storage vs. mesh are decoupled

`Chunk` is pure GL-free storage so the headless server can construct chunks. The GPU mesh lives in a
separate client-only `ChunkRenderData`. This split is the backbone of the whole design — don't refuse it.

```
            WorldBase (abstract: coords, raytrace, Get/SetBlock contract)
            /                                    \
     WorldServer                              WorldClient
     - LoadedChunks: Chunk (storage)          - LoadedChunks: Chunk (received copies)
     - terrain gen / light / save             - RenderData: Vector3i -> ChunkRenderData (GL mesh)
     - 3 background threads                    - 1 background mesh thread

   Chunk  (Blocks/Chunk.cs)            ChunkRenderData  (Client/Graphics/ChunkRenderData.cs)
   - PaletteStorage block ids          - holds a Chunk + two VertexArrayObjects (opaque + transparent)
   - PaletteStorage light (RGB)        - Update() : CPU meshing (ChunkMesher) — safe off-thread
   - block data, min/max bounds        - Upload()/Draw()/Dispose() : GL — main thread ONLY
   - Write(BinaryWriter)               - Upload() gated on `Updated` (see invariants)
```

**Storage is bit-packed paletted, not dense arrays** (`Blocks/PaletteStorage.cs`). A `Chunk` holds two
`PaletteStorage` containers (block ids, packed light) plus the block-data dict and min/max. Each container
is a small palette of the distinct values + a bit-packed index array (`bitsPerEntry = ceil(log2(count))`,
or a single-value fast path with no index array for a uniform chunk). With no skylight the light container
is single-value (~16 B) for nearly every chunk; terrain regions are near-uniform too. This shrinks the
per-chunk clone **and** the resident chunk heap ~10–50× versus the old dense `ushort[4096]` pair (16 KB),
which is what drove the allocation/GC stall that worsened the longer the player moved. The `Chunk.Index`
x/y/z flattening order still defines the layout, so the (de)serializers must iterate in that order.

`ChunkMesher.AddBlockToVao(WorldBase, ...)` reads neighbour blocks through `WorldBase`, so it works for any
world. Chunk serialization (`Chunk.Write` ↔ `new CachedChunk(world, pos, reader)`) is reused for both disk
saves (`WorldSerializer`) and the `ChunkData` network packet; both write each container's palette form via
`PaletteStorage.Write`/`Read`.

**Paletted storage is concurrency-safe by a single-writer + copy-on-grow rule** (see `PaletteStorage`'s
class doc). A published storage's palette and bit-width are immutable; a `Set` reusing an existing value
rewrites one packed entry in place (a benign single-entry torn read, exactly as the old dense `ushort[]`
already tolerated), while a `Set` introducing a new value returns a NEW storage the chunk publishes through
its `volatile` field. Each container has exactly one writer thread (server: block ids = tick thread, light =
light thread; client: both = the apply thread), so concurrent readers (mesher, network serialize, raytrace)
always see a structurally consistent snapshot. **Do not introduce a second writer to either container.**

---

## Networking

`Networking/Packet.cs` defines the `PacketId` enum, the `Packet` base (`Write`/`Read` over
`BinaryWriter`/`BinaryReader`), the id→constructor factory, and `Serialize`/`Deserialize`. Each packet is
its id byte followed by its payload. `TcpConnection` frames packets with a 4-byte little-endian length
prefix and serializes both ways. `LoopbackConnection` (singleplayer) instead **passes the `Packet` object
by reference** — no serialize/deserialize — because both endpoints are pumped sequentially on the client's
main thread and the server builds a fresh packet per `Send` it never reads back, so there's no shared
mutable state to race on (see the performance note). The wire packets are identical; only the in-process
transport shortcuts them.

`ChunkData` follows this through: compression and serialization are **lazy, inside `ChunkDataPacket.Write`/
`Read`** (the TCP transport boundary), not in `From`. `From(chunk)` just carries the live `Chunk` by
reference. Over loopback `Write`/`Read` never run, so the singleplayer streaming path does **no GZip and no
(de)serialize at all** — the carried `Chunk` is cloned by `new Chunk(world, source)` (a paletted copy: small
palette + packed index arrays; uniform chunks copy almost nothing). Over TCP `Write` serializes+GZips; `Read`
**only copies the still-compressed bytes** into `CompressedData` (a cheap memcpy on the receive/main thread)
and the client decompresses + deserializes them later. **Both transports decode on the client's background
apply thread, not the render thread** (see the threading model). The clone tolerates the server mutating the
source chunk concurrently (a torn entry self-corrects via the next `BlockChanges` delta) — the same race the
old server-side `Chunk.Write` already had, made safe by the palette copy-on-grow rule.

```
  Packets (Networking/Packets.cs)
  C→S  Login                 announce
  S→C  LoginAccept           assigns entity id + spawn
  S→C  ChunkData             Vector3i + Chunk (loopback: by ref; TCP: GZip of Chunk.Write)   (initial chunk streaming only)
  S→C  BlockChanges          ChunkPos + (localIndex, blockId, light)[]   (edits + light, see below)
  C→S  ChunkRelease          client dropped a chunk from its cache; clears its SentChunks entry
  C→S  PlaceBlockRequest     pos + block id (id 0 = break)
  C→S/S→C  EntityMove         own player up; relayed to others down
  S→C  EntitySpawn/EntityDespawn   remote players appearing/leaving
```

**Chunk caching & eviction is client-owned.** The client keeps every chunk it receives in memory and, each
`WorldClient.Update`, drops chunks whose centre is farther than `CacheDistance` (384) from the player,
sending a `ChunkRelease` for each. `CacheDistance` is kept comfortably above the server's send range
(`ServerNetwork.ViewDistance`, 256) so a freshly streamed chunk is never evicted-then-re-requested at the
boundary — that gap is the hysteresis that makes revisits free. The server's send loop only ever *adds* to
`SentChunks` (gated so a held chunk is never resent); entries leave `SentChunks` only on `ChunkRelease` or
when the chunk is dirtied and resent. The server-side `UnloadThread` still evicts idle chunks from
`WorldServer.LoadedChunks` (its own memory) — that is unrelated to what the client holds.

**Edits & light propagation reach clients via per-block deltas, not whole-chunk resends.** When the
server applies an edit (`SetBlock`, tick thread) it records a `BlockChange(chunkPos, localIndex, id, light)`
in `WorldServer.BlockChanges`; light propagation (`UpdateThread`) does the same for every node whose light
**actually changed**. The light BFS marks only genuinely-changed nodes — not every chunk it *visits*:
`UpdateLightValues` reads a frontier of unchanged neighbours for lookup but tracks the changed nodes in
`_lightChanged` and only those become `BlockChange`s. `BlockChanges` is a **`ConcurrentDictionary` keyed by
absolute block (chunk pos + local index) with last-write-wins**, not a queue: rapid breaking near a torch
re-lights the same cells across many overlapping floods, so a queue accumulated the same block over and over
(O(floods × volume)) and `FlushBlockChanges`' unbounded drain + per-chunk `List.Add` storm (`AddWithResize`)
trapped the flush thread, getting worse the longer you destroyed (a trace showed it at ~100% of the SP main
thread). Deduping at the source bounds pending changes to O(distinct changed blocks); it's correct because
each `BlockChange` is a full (id, light) snapshot the client applies idempotently (last wins = current state).
`ServerNetwork.FlushBlockChanges()` drains it each tick (enumerate + `TryRemove`, which terminates so a busy
light thread can't trap it), groups changes by chunk, and sends one compact `BlockChanges` packet per chunk
to every session whose `SentChunks` holds it. The client (`WorldClient.ApplyBlockChanges`) mutates the
cached `Chunk` **in place** (`SetBlock` + `SetLightLevel`, no decompress/deserialize) and remeshes only that
chunk plus any **face** neighbour a changed boundary block touches — replicating the old
`MarkChunkAndBoundaryDirty` face logic on the mesh side. (Before this, a single edit near a torch resent
dozens of whole GZip'd chunks; the client decompressed + deserialized each on its **main thread** in
`ApplyChunk` and re-meshed it plus 6 neighbours — the trace showed `DecompressBytes`/`ApplyChunk` dominating
client `Update` and tanking FPS, even though the light maths is cheap and server-side.) Edge/corner-diagonal
AO seams across chunks are a pre-existing limitation unchanged by this (face culling — the only correctness
concern — needs only the direct face neighbour, which is covered).

**Block-data changes still ride whole-chunk resends.** `BlockChanges` carries only id + light, so a block
*data* change (`SetBlockData`, e.g. tinted glass metadata, which affects `ConnectsToBlock` meshing and
`OnLightPassThrough`) still marks `WorldServer.DirtyChunks` and `ServerNetwork.ResendDirtyChunks()` resends
the whole `ChunkData`. This is rare; deltas handle the common place/break/light path. (`BlockChangePacket`,
the old single-block form, was removed — `BlockChangesPacket` supersedes it.)

`ServerNetwork.Pump()` runs once per server tick and does, in order: adopt pending connections → drain &
handle each session's packets (incl. `ChunkRelease` clearing `SentChunks` entries) → drop disconnected
sessions → place the spawn torch once → **stream chunks** (nearest-first, in-range-not-yet-sent only, capped
at `MaxChunksPerTick` per session per tick — no unload pass) → **flush block changes** (delta packets) →
**resend dirty chunks** (block-data only).

---

## Threading model  ⚠️ the load-bearing invariant

```
 WorldServer (background, started in ctor):
   LoadThread    terrain gen + disk load around each player in PlayerEntities; fills _chunksReadyToAdd
   UnloadThread  saves + evicts chunks idle > 30s; fills _chunksReadyToRemove
   UpdateThread  drains _queuedLightUpdates → UpdateLightValues (RGB BFS flood); blocks on
                 _lightSignal (an AutoResetEvent set by SetBlock/SetBlockData) when idle instead of
                 spinning Thread.Sleep(1), waking on a 100 ms timeout to observe _unloaded
   WorldServer.Update()  (caller's thread) drains add/remove into LoadedChunks; runs entity updates

 WorldClient:
   ApplyThread   the SOLE writer of chunk contents: drains _applyQueue in packet order → decodes streamed
                 chunks (SP: clone the carried Chunk; MP: decompress + deserialize) and applies BlockChanges
                 deltas in place → publishes to LoadedChunks → hands positions to the main thread via
                 _renderReady. NO GL. Sleeps on _applySignal when idle.
   MeshThread    drains the mesh queues → ChunkRenderData.Update()  (CPU vertex lists only, NO GL;
                 holds the VAO locks for the whole remesh, so the main-thread upload must not block on them)
   Update()      (MAIN thread) pumps packets (routing ChunkData/BlockChanges to _applyQueue, handling
                 entity/login inline), DrainRenderReady → creates ChunkRenderData (GL) + queues meshing,
                 TryUploads meshed chunks (GL, non-blocking — requeues a chunk being remeshed), evicts, disposes

 Client game loop (MinecraftClone3/Program.cs, 120 Hz, MAIN thread):
   OnUpdateFrame → StateEngine.Update() → StateWorld.Update():
       integratedServer.Update();  network.Pump();   // singleplayer only
       world.SendMove(player);     world.Update();
   OnRenderFrame → StateEngine.Render() → WorldRenderer.RenderWorld(worldClient, projection)
```

**The client chunk pipeline is split across three threads so the render thread does only GL + reads:** the
apply thread decodes/mutates chunk storage (the per-chunk copy that used to dominate the render thread), the
mesh thread builds vertex lists, and the main thread does the GL (render-data creation, upload) plus eviction.
ChunkData and BlockChanges for the same chunk go through **one ordered `_applyQueue`** so a delta can never
race ahead of the chunk it targets (eventual consistency still holds either way — deltas are idempotent and
self-correcting — but the ordered queue avoids a dropped first delta on a not-yet-decoded chunk).

**Invariant 1 — GL calls only on the main thread.** `new VertexArrayObject()` calls `GL.GenVertexArray`,
so even *constructing* a `ChunkRenderData` is a GL call. Therefore `ChunkRenderData`s are created in
`WorldClient.DrainRenderReady` (main thread, inside `Update`), **not** on the apply thread that builds the
`Chunk`; the mesh thread only does CPU meshing (`ChunkRenderData.Update`); and `Upload`/`Draw`/`Dispose`
happen on the main thread. Never move GL off the main thread.

**Invariant 2 — `ChunkRenderData.TryUpload()` is gated on `Updated` and is non-blocking.** The mesh thread
may enqueue the same chunk for upload more than once; `TryUpload` consumes+clears the vertex lists, so a
*redundant* upload would otherwise see empty lists and zero `UploadedCount`, blanking the chunk until the
next re-mesh. The `Updated` flag makes a redundant upload a no-op. Don't remove it. **`TryUpload` must also
never block:** `ChunkRenderData.Update()` (the mesh thread) holds the `_vao`/`_transparentVao` locks for the
*entire* CPU remesh (tens of ms — per-vertex smooth-lighting neighbour sampling), and a single edit remeshes
the chunk **plus up to six face neighbours**. `TryUpload` uses `Monitor.TryEnter`, so if the mesh thread is
mid-remesh of a chunk it returns `false` and `WorldClient` re-queues that chunk for a later frame instead of
the render thread stalling on the lock for the whole remesh — that blocking wait (up to 7 remeshes × tens of
ms in one frame) was the per-edit frame-time spike. The render path (`Draw`/`Sort`/`DrawTransparent`) takes
**no** VAO lock, so rendering is never blocked by meshing; only the upload handoff needed decoupling.

**Invariant 3 — shared collections.** `LoadedChunks` is a `ConcurrentDictionary` (client: the apply thread
adds, the main thread removes-on-evict, both threads + the mesh thread read). `WorldClient.RenderData` is a
`ConcurrentDictionary` but is created/removed **only on the main thread** (`DrainRenderReady`/`UnloadChunk`);
the mesh thread only reads it (a lock-free `TryGetValue`). The renderer does **not** enumerate `RenderData`
each frame — it iterates `WorldClient.RenderList`, a plain `List<ChunkRenderData>` that mirrors `RenderData`'s
values, is touched **only on the main thread**, and is kept in sync O(1) (add in `DrainRenderReady`,
swap-remove in `UnloadChunk` via `ChunkRenderData.RenderListIndex`); see the performance note.
`WorldServer.PlayerEntities` is mutated only via `AddPlayer`/`RemovePlayer`
(locked) and the `LoadThread` snapshots it under the same lock. `DirtyChunks` is a `ConcurrentDictionary`
used as a set. `ServerNetwork._sessions` is touched only on the tick thread (the accept thread merely
enqueues to a concurrent `_pending`). **Per-container single-writer (Invariant 5)** — each `PaletteStorage`
container has exactly one writer thread; see the chunk-model section's copy-on-grow rule.

**Invariant 4 — block-id agreement.** Block ids are assigned by plugin **enumeration order** at load. The
client and server MUST load the same `Plugins/` so ids match (they share `VanillaPlugin`). If this ever
needs hardening, ship `GameRegistry.Save/Load` (a `registry.bin` exchange) — it already exists, unused.

---

## Rendering pipeline (deferred)

```
  WorldRenderer.RenderWorld(WorldClient, projection)
    └─ DrawGeometryFramebuffer → GeometryFramebuffer (MRT G-buffer)
         attachment 0: diffuse   1: normal (w=1 ⇒ "unlit, pass through")   2: baked RGB light   + depth
         · opaque chunks front-to-back, then transparent back-to-front (per-chunk sorted VAO)
         · EntityRenderer  : remote players as solid placeholder cubes (BlockOutline shader, unlit)
         · PlayerController : block-targeting outline
    └─ DrawComposition → screen
         diffuse * max(light, Ambient); if normal.w==1, output diffuse unlit
```

Shaders live in `MinecraftClone3/Content/System/Assets/System/Shaders/`. The world has **no skylight** —
only block-emitted light (e.g. torches) plus a small ambient floor in the composition shader.

OpenGL is capped at **4.1 Core / GLSL 4.10** (macOS limit). Consequences: **uniform and sampler locations
are queried by name** (no `layout(location=)`/`layout(binding=)` on uniforms); vertex-attribute and
fragment-output locations *do* use `layout(location=)`.

---

## State system & game loop

`StateEngine` (static) holds a stack of `_states` plus `_overlays`. `AddOverlay` pauses the base state
(it updates unfocused). `ReplaceState(state)` is **deferred to end of frame** and calls `Exit()` on every
removed layer — that's how a world saves on "Save and Quit to Title" and on window close
(`GameClient.OnUnload → StateEngine.Exit`). State flow:

```
GuiResourceLoading ──(done)──▶ GuiMainMenu ──Singleplayer/Multiplayer──▶ StateWorld
                                    ▲                                        │ Esc
                                    └────────── Save & Quit ◀── GuiPauseMenu ◀┘ (overlay)
```

`StateWorld(window, multiplayer)` builds the connection (loopback+integrated `WorldServer` for SP, or a
`TcpConnection` for MP), creates the `WorldClient`, and logs in. On a failed MP connect it flips back to
the main menu.

---

## Resource & plugin loading

`GuiResourceLoading` (client) and `MinecraftClone3Server/Program.LoadPlugins` (server) mirror each other:
`CommonResources.Load()` → add the `System` plugin → add every dir/zip in `Plugins/` →
`PluginManager.LoadResources` → `LoadPlugins`. **The server stops there; the client additionally does the
GL-only steps** (`ClientResources.Load`, `BoundingBoxRenderer.Load`, `EntityRenderer.Load`,
`BlockTextureManager.Upload`). Plugin model JSON and PNGs are read CPU-side via StbImage, so they load fine
headless; only the texture-array *upload* is GL.

Because server-side light simulation calls `Block.GetLightLevel`, **block code that runs on the server must
not touch client/GL/window state** (this is what crashed `BlockTorch` — it read the keyboard).

Content staging (see the two exe `.csproj` files): the `System` plugin (shared, from
`MinecraftClone3/Content/System`) and `VanillaPlugin` (its content + freshly built DLL under `Dlls/`) are
copied next to each executable so both resolve `Plugins/` against `AppContext.BaseDirectory`.

---

## Debug & profiling tools

In-world keys (handled in `PlayerController.Update`):

- **F3** — toggle the frame profiler. Writes a CSV per render frame to
  `~/.local/share/MinecraftClone3/profiling.csv` (`GamePaths.UserDataDir`). An on-screen `● REC`
  shows while recording; toggling off (or leaving the world) flushes and closes the file.
  Columns: `t, frameMs, fps, updateMs, renderMs, swapMs, gapMs, gpuMs, updCalls, gen0/1/2 (cumulative),
  dGen0/1/2 (per-frame GC events), heapMB, allocMB (allocated per frame, all threads),
  srvMB/netMB/cliMB/rndMB (per-frame main-thread allocation split: integrated server / networking /
  client world / render),
  loadMB/lightMB/unloadMB/meshMB/applyMB (per-frame background-thread allocation split: WorldServer load /
  light / unload threads, client mesh thread, client apply thread — the off-render-thread chunk decode/clone),
  chunks, renderData, pendingMesh, entities,
  pcx/pcy/pcz (player chunk), borderCross (1 the frame the player changes chunk),
  srvMs/netMs/cliMs (the top-level split of updateMs into the three StateWorld.Update calls: integrated
  server sim / network Pump / client world Update — accumulated over the update ticks in the render
  interval, so srvMs+netMs+cliMs ≈ updateMs and a spike is attributable to one of the three),
  streamMs/flushMs/chStreamed/chDrained/chPkts (inside netMs: ServerNetwork.Pump's chunk-streaming vs
  block-change-flush time, chunks streamed this tick, block-changes drained, delta packets sent),
  pktMs/drainMs/upMs/evictMs (inside cliMs: WorldClient.Update's packet handling / DrainRenderReady
  (GL render-data creation) / GL upload loop / chunk eviction), upChunks/upIndices (chunks uploaded and
  total index count uploaded this frame), upQ (upload-queue depth)`. `frameMs` is the
  real frame interval (catches drops); `updateMs`/`renderMs` are CPU work. The four stalls a CPU sampler
  **can't** see, isolated so a high `frameMs` with tiny `updateMs`/`renderMs` is attributable: `swapMs`
  = the `SwapBuffers` call; `gapMs` = OpenTK's between-frame `NewInputFrame`+`ProcessWindowEvents` (the
  GLFW poll, where an **async/vsync present surfaces on Linux/GLX** even though `SwapBuffers` itself
  returned instantly); `gpuMs` = actual GPU render time (`GL_TIME_ELAPSED` query, ping-ponged so reading
  it never stalls). `gpuMs` large ⇒ GPU-bound; `gpuMs` small but `gapMs` large ⇒ present/event overhead,
  not the GPU. When `updateMs` is large instead, srvMs/netMs/cliMs and their sub-splits localize it — e.g.
  `upMs ≈ updateMs` ⇒ the GL upload (re-`BufferData` of edited chunks) is stalling, not lighting/meshing
  (which are off-thread). `updCalls` is `OnUpdateFrame` calls per render frame (OpenTK fixed-timestep
  catch-up); ≫ 1 means updates are running behind and being batched.
  The profiler reads only lock-free mirrors (`WorldClient.LoadedChunkCount`/`MeshQueueDepth`/`UploadQueueDepth`,
  maintained on the writing threads) — **never** `ConcurrentDictionary.Count` (all-stripe lock) or a
  `_meshLock` take — so recording with F3 on doesn't contend with the apply/mesh threads and inflate the
  very stutter it measures.
  Code: `Util/Profiler.cs`, fed from `GameClient.OnRenderFrame` + `StateWorld.Update` (phase times).
- **F4** — toggle chunk-border wireframes around the player (current chunk red, neighbours yellow).
  Code: `Client/Graphics/ChunkBorderRenderer.cs`, drawn in the geometry pass (depth-tested).

External (.NET global tools, no rebuild — attach to the running PID):

```
dotnet tool install -g dotnet-counters dotnet-trace dotnet-gcdump   # once
dotnet-counters monitor -p <pid> System.Runtime                     # live CPU/GC/heap/alloc-rate
dotnet-trace collect   -p <pid> --profile gc-verbose                # GC events -> .nettrace (PerfView/VS)
dotnet-trace collect   -p <pid>                                     # CPU sampling -> .nettrace
dotnet-gcdump collect  -p <pid>                                     # heap snapshot -> .gcdump
```

`pidof MinecraftClone3` (or check `dotnet-counters ps`) to find the PID. Rider/Visual Studio have
built-in CPU + allocation + timeline profilers if preferred.

## Conventions

- **Comments:** self-documenting code. Only `///` XML doc comments where they earn their place — **no
  inline `//` narration** of what the next line does.
- Match the surrounding code's style, naming, and comment density.
- **No backwards compatibility.** The project is in rapid development with no shipped users. Do **not**
  add format-version negotiation, save migrations, deprecation shims, or compatibility fallbacks. When
  the on-disk or wire format changes, the world is simply regenerated (delete `World/`). Prefer the
  clean break over machinery that carries the old shape forward. (Crash-robustness — e.g. regenerating
  a truncated/corrupt chunk rather than killing the load thread — is fine; that is not back-compat.)
- **Record, don't interrupt.** When working through a multi-step task (e.g. the efficiency overhaul),
  do **not** run the game, capture traces, or pause to ask the maintainer to verify between steps — the
  maintainer tests at the end. If you spot a bug, risk, correctness concern, or improvement opportunity
  along the way, **write it into this file** (the "Known rough edges / deferred work" section below)
  instead of stopping. Compile checks (`dotnet build`) are fine; runtime verification is the maintainer's.
- **Flag context rot — don't push through it.** If the conversation has grown long enough that earlier
  detail is starting to blur (you're re-deriving things, losing track of decisions, or a clean focused
  context would serve the next phase better — e.g. at a natural boundary like finishing a design and
  starting a big refactor), **stop and tell the maintainer it's a good time to `/compact`** rather than
  soldiering on with degrading context. Surface it; let them decide.

---

## Performance notes (implemented — don't regress these)

These record *why* hot-path code looks the way it does, so a later change doesn't unknowingly undo a measured
win. They are settled — not open work. (Each was the top allocator/cost in a trace at the time.)

- **Chunk storage is bit-packed paletted** (`PaletteStorage`; see the chunk-model section for the full
  rationale + the copy-on-grow concurrency rule). It replaced dense `ushort[4096]` + `LightLevel[4096]`: a
  trace while moving showed the per-chunk dense clone (two 16 KB arrays) at ~86–92% of the render thread and
  the resident dense heap drove a worsening GC stall (single-heap ephemeral collections scan a growing live
  set). Paletted storage shrinks both ~10–50× — uniform/near-uniform chunks and (no skylight) the all-zero
  light container cost almost nothing. The flat `Chunk.Index(x,y,z) = (x*16+y)*16+z` ordering survives as the
  linear index into the palette's packed array and still defines the (de)serialize iteration order. *(The
  earlier win — flat 1-D over `[16,16,16]` to dodge the `Array.CreateInstanceMDArray` allocator — is folded
  into this: the palette's index array is a single `long[]`, no multidimensional allocation.)*
- **Light BFS reuses presized scratch + a chunk cache.** `WorldServer.UpdateLightValues` reuses
  `_lightSpreadQueue`/`_lightRemoveQueue`/`_lightLevelCache`/`_lightBlockCache`/`_lightChanged` instead of
  allocating six capacity-1024 `Queue<LightNode>` + two dictionaries per call (~22s of `Queue..ctor` in a
  trace). Safe because `UpdateThread` is the sole caller. The dicts are pre-sized (8192) because
  `Dictionary.Resize` of the visited-node memo was the *entire* light-thread cost in a later trace as the
  visited sphere grew. `_lightChanged` separates "visited" (memoised for lookup) from "actually changed" so
  the writeback dirties/enqueues only changed nodes (see the networking note on resend flooding). The
  per-neighbour empty-chunk test goes through `_lightChunkCache` (chunk pos → `Chunk`, memoised for one
  flood) instead of a fresh `LoadedChunks.ContainsKey` probe each visit.
- **Light removal re-spread uses the neighbour's own level.** In the removal BFS, when a neighbour's light is
  `>=` the value being removed it belongs to a stronger source and is pushed to the spread queue to refill the
  hole, enqueued with `nextNodeLightLevel[color]` (its actual level) rather than the removed `node.Value` —
  the latter under-filled and left dark seams after repeated place/break near a light. **Behavioural** change
  to lighting; sanity-check place/break near a torch if touching this.
- **Server background threads reuse per-tick scratch.** `LoadThread` allocates nothing per tick: the player
  snapshot (`_loadPlayersScratch`), per-player candidate lists (`_loadPlayerChunkLists`), in-list dedup
  (`_loadDedup`), and round-robin merge output (`_loadMerged`) are reused; the distance sort uses a cached
  closure-free `Comparison` reading `_loadSortOrigin`; `ExtensionHelper.ZipMerge` fills a caller-owned list
  with a plain loop (no `.ToArray()`/`.Where`/`.Select`/`Aggregate`). `LoadChunk` resolves
  `Vanilla:Grass`/`Vanilla:Dirt` once into `_terrainGrass`/`_terrainDirt`. `UnloadThread` hoists
  `DateTime.Now`, reuses `_unloadScratch`, dropped a pointless `lock(LoadedChunks)`, and dedups atomically via
  `_chunksReadyToRemove.Add` under that set's own lock (the old cross-lock `Contains` was a latent race).
  `WorldServer.Update()` drains the staging collections with `foreach` + `Clear()`, not LINQ `First()` in a
  `while`. `UpdateThread` blocks on `_lightSignal` (AutoResetEvent) when idle instead of `Thread.Sleep(1)`.
- **The mesh upload is non-blocking (`ChunkRenderData.TryUpload`).** `ChunkRenderData.Update()` (mesh thread)
  holds the `_vao`/`_transparentVao` locks for the *entire* CPU remesh — tens of ms, since `ChunkMesher` does
  per-vertex smooth-lighting (~4 `GetBlockLightLevel` + `IsFullBlock` lookups *per vertex*) over the chunk's
  whole min..max box. A single edit remeshes the edited chunk **plus up to six face neighbours**, all queued for
  upload. The main thread's upload used to call a *blocking* `Upload()` that took those same locks, so it stalled
  on each in-progress remesh — up to ~7 remeshes × tens of ms **in one frame** = the ~100 ms per-edit `updateMs`
  spike (pure lock wait, zero allocation, only when editing — idle never remeshes so it was butter-smooth). Now
  `TryUpload` uses `Monitor.TryEnter`; if the mesh thread holds the lock it returns `false` and
  `WorldClient.RequeueUpload` defers that chunk to a later frame. The render path (`Draw`/`Sort`) takes no VAO
  lock, so meshing never blocked rendering — only the upload handoff did. **Don't reintroduce a blocking upload.**
- **Chunk-mesh re-upload orphans the GPU buffer (don't go back to re-`BufferData`-ing live storage).** A
  re-meshed chunk re-uploads its VBOs every edit. Re-specifying the *same* buffer with
  `glBufferData(data, StaticDraw)` while the GPU is still drawing from it forces an implicit CPU↔GPU sync —
  the CPU blocks until that draw finishes. Under a deep frame queue (high-refresh / async present) this was a
  pinned **~100 ms `updateMs` spike per edit**, present *only* while destroying (idle/initial streaming never
  re-upload) and invisible to a CPU sampler (the CPU is *waiting on the GPU*, not running code). The shared
  `GlBuffer.UploadArray` helper now keeps `StaticDraw` for a chunk's **first** upload (most chunks never
  change → optimal residency) and on a **re-upload orphans** the buffer —
  `glBufferData(target, size, IntPtr.Zero, DynamicDraw)` then `glBufferSubData` — so the driver hands back
  fresh storage and retires the old one once the in-flight draw completes (a driver-managed "buffer rename" =
  the render-old-swap-to-new-without-waiting pattern). All three VAOs route through it; the sorted VAO's index
  buffer stays `DynamicDraw` because `Sort()` rewrites it per frame. On Mesa (our driver) this also silences
  the perf-warning-108 "glBufferSubData on a GL_STATIC_DRAW buffer". Orphaning is a *commonly-implemented*
  (not spec-guaranteed) optimization — well-supported on Mesa + macOS GL; if a target ever fails to orphan
  the fallback is an explicit N-buffer ring (the 4.1 cap rules out GL 4.4 persistent-mapped buffers). Ref:
  [Khronos Buffer Object Streaming](https://wikis.khronos.org/opengl/Buffer_Object_Streaming).
- **VAO upload is zero-copy.** `VertexArrayObject`/`SortedVertexArrayObject`/`SpriteVertexArrayObject` upload
  straight from the backing `List<T>` via `ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(list))`
  (synchronous `GL.BufferData`/`BufferSubData` copy during the call, so no pinning issue) instead of
  `list.ToArray()` (was ~46% of main-thread CPU while streaming chunks). Frustum culling
  (`Frustum.SpehereIntersection`) uses a plain loop, not LINQ `.All(lambda)`, and `ServerNetwork.StreamChunks`
  tests interest against reused scratch lists instead of rebuilding a whole-world `HashSet<Vector3i>` per tick.
- **Meshing is allocation-free per face.** `ChunkMesher.AddFaceToVao` used to `new` a
  `Vector3[4]`/`Vector4[4]`×2/`Vector3[4]` + a `uint[6]` index array **per face** (the mesh thread's top
  allocator); it now writes the four vertices straight to the VAO and appends the six indices in place via
  `VertexArrayObject.AddFace(baseVertex, flipped, faceMiddle)` (winding patterns live on the VAO; per-face
  `Vector2[4]` UVs cached once on `BlockModel.FaceData`). `SortedVertexArrayObject.FaceInfo` is a **struct**
  `(faceMiddle, baseVertex, flipped)` — no per-face object, no per-face index array — and its per-frame
  transparency sort rebuilds indices into a reused `List<uint>` uploaded via `BufferSubData`+`AsSpan`. Its
  `_faceInfos`/`_sortedIndices` lists are **allocated lazily on the first transparent face**, not eagerly at
  capacity 1024: every `ChunkRenderData` owns a transparent VAO but most chunks are fully opaque, so eager
  1024-capacity lists were ~24 KB of empty backing arrays per streamed chunk that then stayed resident — once
  the loopback GZip round trip was gone, a trace showed these two `new List<>(1024)` were **71% of the
  main thread while moving** (the dominant cost of `ChunkRenderData..ctor` under `WorldClient.ApplyChunk`).
  Lazy allocation makes opaque chunks pay nothing here. The CPU
  vertex `List<T>`s are recycled through `VaoBufferPool` (thread-safe `ConcurrentBag` per element type):
  `VertexArrayObject.Add` rents on the first vertex (mesh thread), `Clear` returns after the GPU upload (main
  thread), so a remesh allocates no lists steady-state. (Chosen over a per-VAO `Reset()` that keeps capacity:
  that would pin every loaded chunk's CPU mesh in memory; the pool bounds retained buffers to the in-flight
  meshed-not-yet-uploaded working set.) Non-meshing VAOs that build once and never `Clear`
  (Entity/BoundingBox/ChunkBorder renderers) keep their rented lists for their lifetime — negligible.
- **Client per-frame allocations are reused, not re-newed.** `Frustum`/`Plane` are refilled in place:
  `WorldRenderer` keeps one static `Frustum` and calls `Set(viewProjection)` each frame instead of
  `FromViewProjection` newing a `Plane[6]` + six `Plane`s (the duplicate `EntityPlayer.ViewFrustum` built
  every Update was write-only/dead and was removed). `WorldClient.Update` reuses `_toUploadScratch`, **caps
  the packet drain** at `MaxPacketsPerTick` (64) and the render-ready drain at `MaxRenderReadyPerTick` (256)
  so a burst can't process unbounded packets / create unbounded GL render-data in one frame (the heavy
  decode itself is off the render thread on the apply thread). `StateEngine.Update` reuses
  `_overlaysToRemove`, runs the overlay pass as a plain reverse `for` loop, and uses index access not
  `LastOrDefault()`.
- **GUI text and the profiler don't allocate per frame.** `Font.MeasureWidth`/`DrawRun` decode codepoints
  through `NextCodepoint(text, ref i)` instead of the `Codepoints` `yield` iterator, which allocated an
  enumerator per call (a top main-thread allocator — every label is measured *and* drawn each frame). The
  `Codepoints` iterator is kept only for the one-time load paths. `Profiler.Record` (active only under F3, but
  the user profiles with it on, so it polluted its own numbers) formats its ~28-field CSV row into a reused
  `StringBuilder` via stack-buffer `double/long.TryFormat` instead of `string.Join` over per-field
  `ToString`s — InvariantCulture preserved (German locale would otherwise write `,` decimals). It uses
  `GC.GetTotalMemory`/`GetTotalAllocatedBytes`/`CollectionCount`, never the costly `GetGCMemoryInfo`.
  `GuiButton`'s discrete state `Color4`s are `static readonly` (they're structs, so this is tidiness, not an
  alloc win).
- **Region index is KB-sized.** `WorldSerializer.ChunksInRegion = 32` → a `32³ × 8 B = 256 KB` flat index
  (was 128 / 16 MB). `SaveChunk` rewrites the index per saved chunk and `LoadChunk` decompresses it per cache
  miss, so the 64× shrink killed the ~480 MB/frame load-thread allocation that collapsed FPS to ~10; the
  `MaxCachedIndexDatas` LRU (16) now holds a few MB. Both the index write and each chunk append stream
  straight through `GZipStream` to the file (no intermediate `byte[]`); the chunk's stored length comes from
  the append-stream `Position` delta. `ChunksInRegion` defines the on-disk region grid — changing it requires
  regenerating `World/`.
- **Singleplayer chunk streaming is serialize-, GZip-, and copy-free on the produce side.** `LoopbackConnection`
  passes the `Packet` object **by reference** (queues `Packet`, not `byte[]`) — safe because both endpoints are
  pumped sequentially on the client's main thread and the server builds a fresh packet per `Send` it never reads
  back, so there's no shared mutable state to race on (`Serialize`/`Deserialize` were ~64% of `ServerNetwork.Pump`
  in a trace). Building on that, **`ChunkDataPacket` compression/serialization is lazy — inside `Write`/`Read`,
  the TCP transport boundary, not `From`.** `From(chunk)` carries the live `Chunk` by reference; over loopback
  `Write`/`Read` never run (no GZip, no (de)serialize at all), and the carried chunk is cloned via
  `new Chunk(world, source)` — now a **paletted copy** (`PaletteStorage.Clone`: a small palette array + the
  packed `long[]`, race-tolerant like the old server-side `Chunk.Write`), not the old dense two-`[4096]`
  `Array.Copy`. A trace while moving showed the old dense clone was ~86–92% of the **render** thread; it is now
  both ~10–50× smaller (palette) **and off the render thread entirely** (the apply thread — see the threading
  model). TCP is unchanged on the wire; `Read` now just copies the still-compressed bytes and the apply thread
  decompresses + deserializes (so the MP decode is off the render thread too). **The `Chunk(CachedChunk)` ctor
  adopts the `CachedChunk`'s paletted storage by reference** — a cheap handoff, so the server's `Update` drain
  (the render thread in SP) does no chunk copying; the palette build happens on the `LoadThread` during
  `CachedChunk` construction.
- **The client runs Server + Concurrent GC** (`MinecraftClone3.csproj`: `<ServerGarbageCollection>` +
  `<ConcurrentGarbageCollection>`). It was added when chunk streaming was dense-storage **GC-bound** (the
  render-thread clone of two `[4096]` arrays plus the integrated server's terrain-gen pair). Paletted storage +
  off-thread decode now cut both the per-chunk allocation (~10–50×) and the resident live heap that made
  ephemeral GCs scan more the longer the player moved, so the GC pressure this setting fought is largely gone —
  but Server GC still parallelizes the apply/mesh/server background allocations across cores, so keep it. It
  trades memory for pause time; the two csproj lines are the revert switch. (The dedicated server still uses
  default GC — set the same there if its tick stalls under load.)
- **The all-loaded-chunks interest scans are gated on player-chunk change.** `ServerNetwork.StreamChunks`
  (server tick) and `WorldClient.EvictDistantChunks` (client update) used to enumerate the whole
  `LoadedChunks` `ConcurrentDictionary` (~3000 entries) **every frame**; a 2026-06-19 CPU trace showed
  `ConcurrentDictionary.GetEnumerator` at **~88% of CPU**, and the scans spiked to ~103 ms `updateMs` hitches
  when the enumeration raced the apply thread touching `LoadedChunks`. Now each skips the scan unless the
  player crossed a chunk border (`StreamChunks` also rescans while a send backlog remains — capped at
  `MaxChunksPerTick` — and when the loaded-chunk count changes, via `ClientSession.StreamScanChunk/
  StreamScanLoadedCount` and `WorldClient._lastEvictChunk`). Standing still in a streamed area now does **zero**
  per-frame chunk enumeration in update. Safe because chunks only ever stream in within `ViewDistance`
  (< `CacheDistance`), so nothing enters or leaves cache range while the player is stationary.
- **The renderer iterates a main-thread `List`, not the `RenderData` `ConcurrentDictionary`.**
  `WorldRenderer.DrawGeometryFramebuffer` used to `foreach (entry in world.RenderData)` every frame to
  frustum-cull (it can't be gated on player-chunk like the interest scans — the camera rotates). A
  2026-06-19 trace (post-`TryUpload`-fix, captured during destroying) showed the **render thread spending
  ~100% of its samples in `ConcurrentDictionary<Vector3i,…>.GetEnumerator()` under
  `DrawGeometryFramebuffer`** — the per-frame enumeration (an O(bucket-table) walk plus a heap-allocated
  enumerator), which dominates once the rendered set is large. Now `WorldClient.RenderList`
  (a plain `List<ChunkRenderData>`, main-thread only) mirrors `RenderData`'s values — appended in
  `DrainRenderReady`, **swap-removed** in `UnloadChunk` via `ChunkRenderData.RenderListIndex` (O(1), no
  scan) — and the renderer iterates it by index (contiguous, cache-friendly, zero allocation). `RenderData`
  stays a `ConcurrentDictionary` purely for the by-position lookups (`DrainRenderReady`, the upload loop,
  the mesh thread's `TryGetValue`, `UnloadChunk`). The profiler's `renderData` column now reads
  `RenderList.Count` (a field read) instead of `RenderData.Count` (which acquires **all** of the
  dictionary's locks) — that `.Count` ran every frame in `Profiler.Record`, *after* the `renderMs`
  stopwatch stops, so it landed in `frameMs` but not `renderMs`; with F3 on (the maintainer profiles with
  it on) that was unmeasured main-thread overhead.

## Known rough edges / deferred work

- **Light copy-on-grow allocates on the light thread during initial lighting.** Each genuinely-new light
  value entering a chunk's light palette returns a new `PaletteStorage` (copy-on-grow), so the first torch
  flood over a chunk allocates O(distinct light values) small arrays on the server `UpdateThread` (background,
  not the render thread). Subsequent re-lights reuse existing palette values in place (no alloc). Bounded and
  off the hot path; the old dense light array allocated nothing here, so this is a deliberate trade for the
  resident-heap + clone win. If it ever matters, batch `UpdateLightValues`' writeback into one rebuilt light
  container per flood instead of per-block `SetLightLevel`.
- **`PaletteStorage.IndexOf`/`Set` is a linear palette scan.** Fine for block ids (a handful of distinct
  values) but a chunk with a large light palette (smooth RGB near a torch — up to a few hundred distinct
  values) pays O(palette) per `Set` on the light thread. A reverse `value→index` lookup built per grown
  snapshot (immutable, read-only after, so still thread-safe) would make it O(1) — deferred; background cost.
- **Pathological all-distinct chunks store slightly more than dense.** A chunk with hundreds–thousands of
  distinct values grows `bitsPerEntry` toward 12 and the palette toward 4096 entries, so worst case (~14 KB)
  exceeds the old 8 KB-per-container dense — but this never occurs in practice (block types per chunk are few;
  light is 0 across the no-skylight world except near the few torches). No fallback-to-dense is implemented.
- **`PaletteStorage.Read` doesn't validate entries are `< paletteCount`.** It checks count/bits/length, but a
  packed index whose value `≥ count` (only possible from corrupt or buggy-server bytes — a conforming writer
  never emits one, and torn reads only ever flip among valid indices) would index `_palette` out of range in
  `Get`, thrown later on whatever thread first reads it (mesher/raytrace). The disk path fail-safes
  (`WorldSerializer.LoadChunk` try/catch → regenerate) and the TCP decode is wrapped in the apply thread's
  try/catch, but a *post-decode* `Get` throw on the mesh/main thread is not guarded. Left unfixed deliberately:
  it can't fire in normal operation (round-trip is exact), validating would branch the per-block `Get` hot path
  or scan all 4096 entries per decode, and a genuine palette bug *should* surface loudly rather than be masked.
- **Meshing throughput, not stutter, is the remaining per-edit cost.** With the upload no longer blocking on
  the remesh lock (see the performance note on `TryUpload`), the *frame* no longer stalls when editing. But a
  single edit still triggers a **full-chunk remesh of the edited chunk plus up to six face neighbours**, and one
  remesh is tens of ms (per-vertex smooth-lighting samples ~4 `GetBlockLightLevel` + `IsFullBlock` *per vertex*,
  over the chunk's whole min..max bounding box) on a **single** mesh thread. So under rapid continuous editing
  the mesh queue can grow and the *visual* update of edited chunks lags (latency), even though the framerate
  stays smooth. If that latency matters: remesh only the affected sub-region instead of the whole chunk, cache
  per-vertex brightness, or parallelise the mesh thread. (The earlier "~10 FPS / ~100 ms `updateMs` spikes when
  destroying" was **misdiagnosed** as swap/GPU-bound — it was the main thread blocking in `ChunkRenderData.Upload`
  on the mesh thread's VAO lock during these remeshes. The F3 CSV that looked like a 10 FPS *baseline* was
  captured during *continuous* destroying; standing idle renders fine, confirming the pipeline/GPU was never the
  bottleneck.)
- **Chunk saves are per-chunk, not batched per region.** `UnloadThread`/`Unload()` call `SaveChunk` once per
  dirty chunk, each doing one 256 KB index rewrite. Batching all of a region's dirty chunks into a single
  index rewrite is deferred — marginal now that the index is 64× smaller, and `SaveChunk` already early-outs
  on `!NeedsSaving`.
- **`BlockTintedGlass.OnPlaced` reads `ClientResources.Window.KeyboardState`** — client/window state touched
  from block code that runs **server-side** (`PlaceBlock` → `OnPlaced`). On the headless dedicated server this
  throws the moment tinted glass is placed (same class of bug as the old `BlockTorch` keyboard read). Works in
  singleplayer only because the integrated server shares the client process. Placement metadata should come
  from the place *request*, not a live keyboard read on the server.
- No movement interpolation for remote players (they snap to the last received position).
- `StateWorld` connects synchronously on the main thread; a far/unreachable MP host briefly blocks.
- `ClientSession.SentChunks` shrinks only on `ChunkRelease`/dirty resend, so a misbehaving or crashed client
  could leave stale entries until it disconnects. Bounded in practice by client `CacheDistance` eviction;
  fine for a hobby project, would need a server-side cap/timeout for hardening.

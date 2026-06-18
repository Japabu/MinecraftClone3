# CLAUDE.md

> **⚠️ KEEP THIS FILE UP TO DATE.** This is the single source of truth for how the project fits
> together. Whenever you change architecture, threading, the wire protocol, the build/run flow, an
> invariant, or a convention, **update the relevant section in the same change** — a stale diagram is
> worse than none. If a task proves something here is wrong, fix it here too. Treat editing this file
> as part of "done," not an afterthought.

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
   - ushort[4096] block ids (flat)     - holds a Chunk + two VertexArrayObjects (opaque + transparent)
   - LightLevel[4096] (RGB, flat)      - Update() : CPU meshing (ChunkMesher) — safe off-thread
   - block data, min/max bounds        - Upload()/Draw()/Dispose() : GL — main thread ONLY
   - Write(BinaryWriter)               - Upload() gated on `Updated` (see invariants)
```

`ChunkMesher.AddBlockToVao(WorldBase, ...)` reads neighbour blocks through `WorldBase`, so it works for any
world. Chunk serialization (`Chunk.Write` ↔ `new CachedChunk(world, pos, reader)`) is reused for both disk
saves (`WorldSerializer`) and the `ChunkData` network packet.

---

## Networking

`Networking/Packet.cs` defines the `PacketId` enum, the `Packet` base (`Write`/`Read` over
`BinaryWriter`/`BinaryReader`), the id→constructor factory, and `Serialize`/`Deserialize`. Each packet is
its id byte followed by its payload. `TcpConnection` frames packets with a 4-byte little-endian length
prefix; `LoopbackConnection` serializes to `byte[]` too (no shared mutable state) so both paths behave
identically.

```
  Packets (Networking/Packets.cs)
  C→S  Login                 announce
  S→C  LoginAccept           assigns entity id + spawn
  S→C  ChunkData             Vector3i + GZip of Chunk.Write   (the only block transport, see below)
  C→S  ChunkRelease          client dropped a chunk from its cache; clears its SentChunks entry
  C→S  PlaceBlockRequest     pos + block id (id 0 = break)
  C→S/S→C  EntityMove         own player up; relayed to others down
  S→C  EntitySpawn/EntityDespawn   remote players appearing/leaving
       BlockChange           DEFINED but currently UNUSED — edits propagate via dirty ChunkData resend
```

**Chunk caching & eviction is client-owned.** The client keeps every chunk it receives in memory and, each
`WorldClient.Update`, drops chunks whose centre is farther than `CacheDistance` (384) from the player,
sending a `ChunkRelease` for each. `CacheDistance` is kept comfortably above the server's send range
(`ServerNetwork.ViewDistance`, 256) so a freshly streamed chunk is never evicted-then-re-requested at the
boundary — that gap is the hysteresis that makes revisits free. The server's send loop only ever *adds* to
`SentChunks` (gated so a held chunk is never resent); entries leave `SentChunks` only on `ChunkRelease` or
when the chunk is dirtied and resent. The server-side `UnloadThread` still evicts idle chunks from
`WorldServer.LoadedChunks` (its own memory) — that is unrelated to what the client holds.

**Edits & light propagation reach clients via whole-chunk resends, not per-block packets.** When the
server applies an edit it marks the chunk (and boundary neighbours) in `WorldServer.DirtyChunks`; light
propagation marks only the chunks where a block's light level **actually changed** — not every chunk the
BFS *visits*. (The BFS reads a frontier of unchanged neighbours for lookup; `UpdateLightValues` tracks the
genuinely-changed nodes in `_lightChanged` and writes back/dirties only those. Dirtying the whole visited
sphere instead made a single edit near a light source resend dozens of chunks, flooding the client — which
decompresses + deserializes each resend on its **main thread** in `WorldClient.ApplyChunk` and re-meshes it
plus 6 neighbours — and tanked client FPS even though the light maths is cheap and server-side.)
`ServerNetwork.ResendDirtyChunks()` resends fresh `ChunkData` for dirty chunks to every session that has
them. (`BlockChangePacket` exists as a future optimization.)

`ServerNetwork.Pump()` runs once per server tick and does, in order: adopt pending connections → drain &
handle each session's packets (incl. `ChunkRelease` clearing `SentChunks` entries) → drop disconnected
sessions → place the spawn torch once → **stream chunks** (nearest-first, in-range-not-yet-sent only, capped
at `MaxChunksPerTick` per session per tick — no unload pass) → **resend dirty chunks**.

---

## Threading model  ⚠️ the load-bearing invariant

```
 WorldServer (background, started in ctor):
   LoadThread    terrain gen + disk load around each player in PlayerEntities; fills _chunksReadyToAdd
   UnloadThread  saves + evicts chunks idle > 30s; fills _chunksReadyToRemove
   UpdateThread  drains _queuedLightUpdates → UpdateLightValues (RGB BFS flood)
   WorldServer.Update()  (caller's thread) drains add/remove into LoadedChunks; runs entity updates

 WorldClient:
   MeshThread    drains the mesh queues → ChunkRenderData.Update()  (CPU vertex lists only, NO GL)
   Update()      (MAIN thread) pumps packets, applies chunks, uploads meshed chunks (GL), disposes

 Client game loop (MinecraftClone3/Program.cs, 120 Hz, MAIN thread):
   OnUpdateFrame → StateEngine.Update() → StateWorld.Update():
       integratedServer.Update();  network.Pump();   // singleplayer only
       world.SendMove(player);     world.Update();
   OnRenderFrame → StateEngine.Render() → WorldRenderer.RenderWorld(worldClient, projection)
```

**Invariant 1 — GL calls only on the main thread.** `new VertexArrayObject()` calls `GL.GenVertexArray`,
so even *constructing* a `ChunkRenderData` is a GL call. Therefore `ChunkRenderData`s are created in
`WorldClient.ApplyChunk` (which runs on the main thread inside `Update`), the mesh thread only does CPU
meshing (`ChunkRenderData.Update`), and `Upload`/`Draw`/`Dispose` happen on the main thread. Never move GL
off the main thread.

**Invariant 2 — `ChunkRenderData.Upload()` is gated on `Updated`.** The mesh thread may enqueue the same
chunk for upload more than once; `Upload` consumes+clears the vertex lists, so a *redundant* upload would
otherwise see empty lists and zero `UploadedCount`, blanking the chunk until the next re-mesh. The
`Updated` flag makes a redundant upload a no-op. Don't remove it.

**Invariant 3 — shared collections.** `LoadedChunks` is a `ConcurrentDictionary`. `WorldServer.PlayerEntities`
is mutated only via `AddPlayer`/`RemovePlayer` (locked) and the `LoadThread` snapshots it under the same
lock. `DirtyChunks` is a `ConcurrentDictionary` used as a set. `ServerNetwork._sessions` is touched only on
the tick thread (the accept thread merely enqueues to a concurrent `_pending`).

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
  Columns: `t, frameMs, fps, updateMs, renderMs, gen0/1/2 (cumulative), dGen0/1/2 (per-frame GC
  events), heapMB, allocMB (allocated per frame, all threads), srvMB/netMB/cliMB/rndMB (per-frame
  main-thread allocation split: integrated server / networking / client world / render),
  loadMB/lightMB/unloadMB/meshMB (per-frame background-thread allocation split: WorldServer load /
  light / unload threads, client mesh thread), chunks, renderData, pendingMesh, entities,
  pcx/pcy/pcz (player chunk), borderCross (1 the frame the player changes chunk)`. `frameMs` is the
  real frame interval (catches drops); `updateMs`/`renderMs` are CPU work excluding the vsync wait.
  Code: `Util/Profiler.cs`, fed from `GameClient.OnRenderFrame`.
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
- **Fonts:** the Mojang/Minecraft font assets are gitignored and not redistributed; code must degrade
  gracefully without them.
- Match the surrounding code's style, naming, and comment density.

---

## Known rough edges / deferred work

- **Chunk storage is flat 1-D arrays.** `Chunk`/`CachedChunk` hold `ushort[4096]` + `LightLevel[4096]`
  indexed via `Chunk.Index(x,y,z) = (x*16+y)*16+z`, not `[16,16,16]`. The multidimensional arrays hit the
  runtime's slow `Array.CreateInstanceMDArray` allocator, which a trace showed was the single biggest
  cross-thread cost (~9s main + ~12s load) — it fired on every chunk construction, loopback deserialize, and
  disk load. Flat arrays also speed neighbour reads in the mesher. `Write`/`CachedChunk` iterate x/y/z in the
  same order as `Index`, so the **disk/wire format is unchanged**.
- The light-propagation BFS (`WorldServer.UpdateLightValues`) reuses scratch queues/dicts
  (`_lightSpreadQueue`/`_lightRemoveQueue`/`_lightLevelCache`/`_lightBlockCache`/`_lightChanged`) instead of
  allocating six capacity-1024 `Queue<LightNode>` + two dictionaries per call — that was the top
  background-thread allocator in the trace (~22s of `Queue..ctor`). Safe because `UpdateThread` is the sole
  caller. The dicts are pre-sized (8192) because `Dictionary.Resize` of the visited-node memo was the *entire*
  light-thread cost in a later trace as the visited sphere grew. `_lightChanged` separates "visited" (memoised
  for lookup) from "actually changed" so the writeback dirties only changed chunks (see the networking note
  on resend flooding) — previously the writeback re-`SetBlockLightLevel`'d every visited node, re-dirtying and
  resetting `NeedsSaving` on chunks whose light never changed.
- Loopback still serializes + GZips every chunk in-process (wasteful for SP): chunk `MemoryStream`
  (de)serialize + GZip is now the top remaining *main-thread* cost while moving (the MD-array allocator that
  used to dominate it is gone). A reference-passing loopback fast path is the planned next optimization.
- **VAO upload is zero-copy.** `VertexArrayObject`/`SortedVertexArrayObject` upload mesh data straight from
  the backing `List<T>` via `ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(list))` (synchronous
  `GL.BufferData` copies during the call, so no pinning issue) instead of `list.ToArray()`. The old
  `.ToArray()` per buffer per chunk was ~46% of main-thread CPU while streaming chunks. Frustum culling
  (`Frustum.SpehereIntersection`) uses a plain loop, not LINQ `.All(lambda)` (no per-call closure), and
  `ServerNetwork.StreamChunks` tests sent-chunk interest per chunk against reused scratch lists instead of
  rebuilding a whole-world `HashSet<Vector3i>` every tick.
- No movement interpolation for remote players (they snap to the last received position).
- `StateWorld` connects synchronously on the main thread; a far/unreachable MP host briefly blocks.
- Per-tick chunk *send* scan still walks all loaded chunks per session (CPU, not wire); fine at current
  scale. A `ChunkRequest` (client-pull) protocol would let the server send only what a client asks for.
- `ClientSession.SentChunks` shrinks only on `ChunkRelease`/dirty resend, so a misbehaving or crashed client
  could leave stale entries until it disconnects. Bounded in practice by client `CacheDistance` eviction;
  fine for a hobby project, would need a server-side cap/timeout for hardening.
- **Region index is oversized.** `WorldSerializer` uses `ChunksInRegion = 128`, so each region's flat
  index is `128³ × 8 B = 16 MB`. `SaveChunk` rewrites that whole 16 MB (compressed) per saved chunk, and
  `LoadChunk` decompresses it per cache miss. The `MaxCachedIndexDatas` LRU cache (16) is sized to hold a
  player's full in-range region set (≤8 octants) so the load thread stops re-decompressing 16 MB on every
  move — this was the cause of the ~480 MB/frame load-thread allocation that collapsed FPS to ~10. The
  real fix is to shrink `ChunksInRegion` (≈16–32) so the index is KB-sized; deferred because it changes
  the on-disk region format (existing saves under `World/` would need regenerating).

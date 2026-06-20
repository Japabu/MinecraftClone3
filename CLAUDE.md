# CLAUDE.md

> **⚠️ KEEP THIS FILE UP TO DATE.** This is the single source of truth for how the project fits
> together. Whenever you change architecture, threading, the wire protocol, the build/run flow, an
> invariant, or a convention, **update the relevant section in the same change** — a stale diagram is
> worse than none. If a task proves something here is wrong, fix it here too. **Prune as you go:** when a
> "Known rough edges / deferred work" item is resolved, delete it or move its rationale to a permanent
> section (e.g. "Performance notes") — that list is for *open* work only, not a changelog of past fixes.
> Treat editing this file as part of "done," not an afterthought.

A from-scratch Minecraft-like voxel engine in C# on OpenTK (OpenGL). Custom deferred renderer, plugin
system, chunked world with RGB block-light + sky-light propagation and a dynamic day/night cycle, and
**client/server multiplayer** (singleplayer runs an in-process server over a loopback connection;
multiplayer connects to a dedicated server over TCP).

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

- **`WorldServer`** (`Blocks/WorldServer.cs`): the authority. Block storage, terrain gen, RGB block-light +
  sky-light propagation, save/load, entity simulation. **No meshing, no GL** — it can run fully headless.
- **`WorldClient`** (`Client/Blocks/WorldClient.cs`): the client replica. Holds chunks streamed from the
  server, **caches them and owns their eviction** (drops a chunk past `CacheDistance`, then sends a
  `ChunkRelease`), meshes them, renders them, holds remote entities. **No terrain gen, no disk, no lighting.**
- **`ServerNetwork`** (`Networking/ServerNetwork.cs`): per-client sessions, interest-based chunk streaming,
  dirty-chunk resends, entity relay, the TCP listener.
- **Authority:** server owns blocks + light (block + sky). Position is **client-authoritative** (there is no server-side
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
   - PaletteStorage sky (0..15)        - Upload()/Draw()/Dispose() : GL — main thread ONLY
   - block data, min/max bounds        - Upload() gated on `Updated` (see invariants)
   - Write(BinaryWriter)
```

**Storage is bit-packed paletted, not dense arrays** (`Blocks/PaletteStorage.cs`). A `Chunk` holds three
`PaletteStorage` containers (block ids, packed RGB light, sky light) plus the block-data dict and min/max.
Each container is a small palette of the distinct values + a bit-packed index array (`bitsPerEntry =
ceil(log2(count))`, or a single-value fast path with no index array for a uniform chunk). The block-light
container is single-value (~16 B, all-zero) away from torches; the **sky** container is single-value (all-zero)
for underground chunks, and only the surface chunks (and dug caves) carry a small mixed `{0,15}` palette.
(All-air chunks above terrain *are* seeded all-15, but they stay `IsEmpty` and are discarded, never streamed —
the client falls back to sky 15 for any unloaded chunk; see "Known rough edges".) `SetSkyLight` deliberately
does **not** expand min/max (sky fills air everywhere; doing so would defeat that fast path, blow up the
mesher's `min..max` loop, and break `IsEmpty`).
This shrinks the per-chunk clone **and** the resident chunk heap ~10–50× versus dense arrays. The
`Chunk.Index` x/y/z flattening order still defines the layout, so the (de)serializers must iterate in that
order.

`ChunkMesher.AddBlockToVao(WorldBase, ...)` reads neighbour blocks through `WorldBase`, so it works for any
world. Chunk serialization (`Chunk.Write` ↔ `new CachedChunk(world, pos, reader)`) is reused for both disk
saves (`WorldSerializer`) and the `ChunkData` network packet; both write each container's palette form via
`PaletteStorage.Write`/`Read`.

**Paletted storage is concurrency-safe by a single-writer + copy-on-grow rule** (see `PaletteStorage`'s
class doc). A published storage's palette and bit-width are immutable; a `Set` reusing an existing value
rewrites one packed entry in place (a benign single-entry torn read, exactly as the old dense `ushort[]`
already tolerated), while a `Set` introducing a new value returns a NEW storage the chunk publishes through
its `volatile` field. Each container has exactly one writer thread (server: block ids = tick thread, light
and sky = light/Update thread — plus the LoadThread seeds sky at gen, before the chunk is published, so it
is not yet shared; client: all three = the apply thread), so concurrent readers (mesher, network serialize,
raytrace) always see a structurally consistent snapshot. **Do not introduce a second writer to any container.**

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
  S→C  BlockChanges          ChunkPos + (localIndex, blockId, light, sky)[]   (edits + block-light + sky-light, see below)
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
server applies an edit (`SetBlock`, tick thread) it records a `BlockChange(chunkPos, localIndex, id, light, sky)`
in `WorldServer.BlockChanges`; block-light *and* sky-light propagation (`UpdateThread`, see the threading
model) do the same for every node whose value **actually changed**. The light BFS marks only genuinely-changed
nodes — not every chunk it *visits*: `UpdateLightValues`/`UpdateSkyValues` read a frontier of unchanged
neighbours for lookup but track the changed nodes in `_lightChanged`/`_skyChanged` and only those become
`BlockChange`s. Each `BlockChange` is a **full `(id, light, sky)` snapshot** read from the live chunk, so the
block-light writeback and the sky writeback overwriting the same cell (last-write-wins) still converge. `BlockChanges` is a **`ConcurrentDictionary` keyed by
absolute block (chunk pos + local index) with last-write-wins**, not a queue: rapid breaking near a torch
re-lights the same cells across many overlapping floods, so a queue accumulated the same block over and over
(O(floods × volume)) and `FlushBlockChanges`' unbounded drain + per-chunk `List.Add` storm (`AddWithResize`)
trapped the flush thread, getting worse the longer you destroyed (a trace showed it at ~100% of the SP main
thread). Deduping at the source bounds pending changes to O(distinct changed blocks); it's correct because
each `BlockChange` is a full (id, light, sky) snapshot the client applies idempotently (last wins = current state).
`ServerNetwork.FlushBlockChanges()` drains it each tick (enumerate + `TryRemove`, which terminates so a busy
light thread can't trap it), groups changes by chunk, and sends one compact `BlockChanges` packet per chunk
to every session whose `SentChunks` holds it. The client (`WorldClient.ApplyBlockChanges`) mutates the
cached `Chunk` **in place** (`SetBlock` + `SetLightLevel` + `SetSkyLight`, no decompress/deserialize) and
remeshes only that chunk plus any **face** neighbour a changed boundary block touches — replicating the old
`MarkChunkAndBoundaryDirty` face logic on the mesh side. (Before this, a single edit near a torch resent
dozens of whole GZip'd chunks; the client decompressed + deserialized each on its **main thread** in
`ApplyChunk` and re-meshed it plus 6 neighbours — the trace showed `DecompressBytes`/`ApplyChunk` dominating
client `Update` and tanking FPS, even though the light maths is cheap and server-side.) Edge/corner-diagonal
AO seams across chunks are a pre-existing limitation unchanged by this (face culling — the only correctness
concern — needs only the direct face neighbour, which is covered).

**Block-data changes still ride whole-chunk resends.** `BlockChanges` carries only id + light + sky, so a block
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
   UpdateThread  drains _queuedLightUpdates → UpdateLightValues (RGB BFS flood) then UpdateSkyValues
                 (sky BFS flood, same edited cell) per edit; blocks on _lightSignal (an AutoResetEvent set
                 by SetBlock/SetBlockData) when idle instead of spinning Thread.Sleep(1), waking on a 100 ms
                 timeout to observe _unloaded. Sole writer of both the light and sky containers (post-publish)
   WorldServer.Update()  (caller's thread) drains add/remove into LoadedChunks; runs entity updates

 WorldClient:
   ApplyThread   the SOLE writer of chunk contents: drains _applyQueue in packet order → decodes streamed
                 chunks (SP: clone the carried Chunk; MP: decompress + deserialize) and applies BlockChanges
                 deltas in place → publishes to LoadedChunks → hands positions to the main thread via
                 _renderReady. NO GL. Sleeps on _applySignal when idle.
   MeshThread    drains the mesh queues → ChunkRenderData.Update()  (CPU vertex lists only, NO GL;
                 holds the VAO locks for the whole remesh, so the main-thread upload must not block on them);
                 also computes the chunk's occlusion Connectivity graph + SkyExposed flag (plain-field writes,
                 read lock-free on the main thread by the visibility BFS — benign torn read self-corrects next remesh)
   Update()      (MAIN thread) pumps packets (routing ChunkData/BlockChanges to _applyQueue, handling
                 entity/login inline), DrainRenderReady → creates ChunkRenderData (GL) + queues meshing,
                 TryUploads meshed chunks (GL, non-blocking — requeues a chunk being remeshed), evicts, disposes
   RenderWorld() (MAIN thread) BuildVisibleSet runs the camera-chunk visibility BFS (reads Connectivity), then
                 the shadow + geometry + composition passes

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
    └─ BuildVisibleSet → occlusion-cull from the camera chunk (visibility-graph BFS, see below); fills the
         opaque/transparent draw lists ~front-to-back and flags _anyShadowReceiver (a visible sky-exposed chunk)
    └─ DrawShadowMap (only while the sun is up AND _anyShadowReceiver) → ShadowFramebuffer (depth-only)
         CascadeCount cascades, one depth layer each (Texture2DArray); opaque chunks re-drawn from the
         sun's orthographic POV per cascade (ShadowDepth shader, no colour output)
    └─ DrawGeometryFramebuffer → GeometryFramebuffer (MRT G-buffer)
         attachment 0: diffuse   1: normal (w=1 ⇒ "unlit, pass through")
         2: RGBA8 light (rgb = baked block light, a = baked sky-light factor 0..1)   + depth
         · opaque chunks front-to-back, then transparent back-to-front (per-chunk sorted VAO)
         · EntityRenderer  : remote players as solid placeholder cubes (BlockOutline shader, unlit)
         · PlayerController : block-targeting outline
    └─ DrawShadowResolve (same gate as DrawShadowMap) → ShadowResolveFramebuffer (HALF-res RGBA8)
         the 12-tap cascaded PCF runs here at half res (quarter the pixels): r = shadow factor, g = norm
         view depth, b = cascade idx; reads G-buffer normal/depth/light + the cascade depth array
    └─ DrawComposition → screen
         shadow = depth-aware (joint-bilateral) upsample of the half-res resolve buffer (1=lit, early-outed
         past ShadowDistance / in caves / at night); skyLight = sky.a*(shadow*uSunColor*uSunFade + uSkyAmbient);
         light = max(blockLight.rgb, skyLight); diffuse * max(light, MinLight); normal.w==1 ⇒ diffuse unlit
```

Shaders live in `MinecraftClone3/Content/System/Assets/System/Shaders/`. Lighting is block-emitted RGB light
(torches) **plus sky light** modulated by a **dynamic day/night cycle**: the per-vertex light is a `vec4`
(rgb = smooth-lit block brightness, a = smooth-lit sky-occlusion factor); the composition multiplies the sky
factor by the **sun term** (`shadow * uSunColor * uSunFade`, `WorldRenderer.SunColor` — bright warm white at
noon, orange at the horizon, faded out at night) **plus an ambient sky term** (`uSkyAmbient`,
`WorldRenderer.SkyAmbient` — soft blue fill in daytime shadows, dim cool **moonlight** at night). Because
**both** are scaled by the baked sky factor, sky-occluded **caves get no ambient or moonlight and stay dark**
unless a block light reaches them; only a tiny `MinLight` floor (≈ 0, tunable; the old global `Ambient = 0.2`
that lit everything is **gone**) keeps unlit surfaces from being a literal void. The whole thing animates
**with no remesh** (the sky channel is baked into the chunk mesh, so geometry/occlusion is static; only the
sun/moon *colour/intensity* animate). The clock is client-local — MP clients are not yet time-synced; see
"Known rough edges". **Moonlight is non-directional ambient** (no moon shadow pass) — deferred.

**Directional sun shadows (cascaded).** `DrawShadowMap` renders **cascaded shadow maps** (CSM) into
`ShadowFramebuffer` — a single depth `Texture2DArray` of `ShadowFramebuffer.MaxCascadeCount` (4) layers,
`ShadowMapSize` (1024) each (the active `WorldRenderer.CascadeCount` cascades — **default 2**, runtime-settable
up to 4 — see below). **The default deliberately favours soft, low-resolution shadows over crisp ones:** fewer
cascades + a small map make the near cascade's texels large, so its shadows are soft instead of razor-sharp
(per-texel softness reads as world-space softness only because the texels are coarse), and dropping cascades
also cuts depth passes — the dominant surface GPU cost. Crank `ShadowCascades`/`ShadowMapSize` back up for
sharp AAA-style shadows at more passes; the low-res default is an art-direction choice, not a limitation.
The camera view frustum is split by distance into cascades over
`[WorldRenderer.ShadowNear, WorldRenderer.ShadowDistance]` (160) via the standard practical split scheme
(`CascadeSplit`, a `CascadeSplitLambda` blend of logarithmic and uniform partitioning), so near cascades are
crisp and far cascades trade resolution for coverage. Each cascade is fit to the **analytic bounding sphere**
of its frustum slice: the centre rides the camera forward axis and the **radius depends only on the slice
near/far + FOV (read from the projection matrix), so it is constant as the camera rotates → no size shimmer**.
The projection is **deliberately NOT texel-snapped**: the sun advances every frame (day/night cycle), and
snapping to the *rotating* light-space texel grid quantizes the shadow's smooth crawl into ~20 Hz whole-texel
jumps — the visible flicker. Unsnapped, the projection follows the sun smoothly and the 6×6-effective PCF keeps
camera-motion shimmer soft. (Texel snapping is a best practice for a *static* sun + moving camera; a fast
moving sun inverts the trade — see "Known rough edges".) The sun direction comes
from `WorldRenderer.SunDirection()` (same `_dayClock` as the colour, so the brightest sun is the highest sun).
The pass re-draws the **already-uploaded opaque chunk VAOs** (no remesh) with the trivial `ShadowDepth` shader,
once per cascade (the FBO's depth attachment is re-pointed at each layer via `FramebufferTextureLayer`),
frustum-culling chunks against that cascade's light frustum; `PolygonOffset` + a per-cascade normal-offset
(scaled by the cascade's world-units-per-texel) + a small depth bias fight self-shadow acne (culling is
**off** — the voxel mesher emits only single-sided exposed faces). The depth array is a **hardware shadow
sampler** (`CompareRefToTexture` + `Linear`), so each PCF tap is a free 2×2 bilinear comparison; the
shader takes a **12-tap Poisson disc, rotated per pixel** (interleaved-gradient-noise angle) of radius
`uShadowSoftness` texels, giving a soft band-free penumbra (raise it for softer shadows at **no** extra tap
cost). **Two look knobs are uniforms driven by `WorldRenderer` fields, so they're tunable with no
shader recompile:** `WorldRenderer.ShadowSoftness` (penumbra radius, default 8) and `WorldRenderer.ShadowStrength`
(0..1, default 0.65) — the latter (a composition uniform) lifts the shadow floor via `litShadow = mix(1, shadow,
uShadowStrength)` so a fully-shadowed surface keeps some direct sun instead of crushing to ambient-only (the
"shadows too dark" fix); it scales **only** the direct-sun term, so ambient fill and caves are untouched.

**The PCF runs at HALF resolution (`DrawShadowResolve` → `ShadowResolve.fs`), not per full-res pixel.** A
2026-06-20 capture (per-pass `GL_TIMESTAMP` timers, see profiling) showed the old full-res 12-tap PCF inside
composition was **~45 % of the GPU frame** (10.8 ms — the *single* biggest pass, more than the shadow depth
passes or the G-buffer, even though it's one fullscreen draw), because PCF is fill-bound and our integrated
GPU (UHD 630) is fill-limited. So the PCF now runs in a dedicated fullscreen pass into a **half-res RGBA8
target** (`ShadowResolveFramebuffer`, quarter the invocations): `r` = the 0..1 shadow factor, `g` = normalized
view depth (for the upsample), `b` = cascade index (F6 debug). `ShadowResolve.fs` does the world-pos
reconstruction (`uViewProjectionInv`), cascade pick (`uView` · `uCascadeSplits`), light-space projection
(`uLightViewProj[c]`), **15 %-of-split next-cascade blend** (no seam) and **10 %-of-`ShadowDistance` far fade**
(no hard edge). Composition then **depth-aware (joint-bilateral) upsamples** the half-res buffer back to full
res: a 2×2 tap weighted by bilinear position **and** by how close each tap's stored depth (`g`) is to the
pixel's (`exp(-|Δdepth|·DepthSharpness)`), so the half-res shadow doesn't bleed across silhouette edges
(`DepthSharpness` in `Composition.fs` is the tunable edge-vs-blockiness knob). The result is the same 0..1
`shadow` factor multiplying **only** the direct sun part of the sky term — ambient sky fill (`uSkyAmbient`) and
block light untouched, so a *sky-exposed* shadow falls back to the blue sky fill (not black), a *sky-occluded*
one (a cave) goes dark.
Both the resolve and the upsample are **early-outed** where the sun term can't matter — past the last
cascade's range (`viewDepth ≥ ShadowDistance`), sky-occluded (`uLight.a ≈ 0`, caves/interiors), or at
night/dusk (`uSunFade ≈ 0`) — the bulk of the fullscreen win (a wide view is mostly far/occluded terrain).
The resolve pass shares `DrawShadowMap`'s gate (`sunUp && _anyShadowReceiver && Shadows`); when skipped the
half-res buffer is stale but composition's identical early-outs never sample it. Both passes fall under the
`compMs` per-pass timer (so before/after `compMs` is directly comparable).
**Dusk/night handling — the whole directional sun term fades, not just the shadows.** A naive "skip the
shadow pass below the horizon" toggles `shadow` from its real value to 1 at the cutoff, but `uSunColor` is
still bright orange there (it only dims to night *below* the horizon), so every shadowed surface pops bright.
Instead `WorldRenderer.SunFade(toSun.Y)` is a smoothstep over `[ShadowFadeLow, ShadowFadeHigh]` (sun
altitude) that ramps a single `uSunFade` (0..1); the composition multiplies the **direct sun term**
(`sky.a * shadow * uSunColor * uSunFade`) by it. So as the sun sets, sunlit surfaces dim *down to meet* the
shadowed ones and everything converges to the ambient sky term — which is itself crossfading from daytime
fill to **moonlight** (`SkyAmbient`) — with no brightening pop; the sun shadows become irrelevant (sun term
≈ 0) exactly where the pass cuts off (`uSunFade = 0 ⇔ toSun.Y ≤ ShadowFadeLow ⇔` shadow pass skipped, so
`uSunFade > 0` also gates shadow sampling). Block light and the ambient sky term are never scaled by
`uSunFade`, so torches and the moonlight are unaffected. It is **main-thread GL** like every
other pass (Invariant 1 holds — it only re-draws existing VAOs, allocates nothing per frame, and does no
meshing).

**Cascade count is runtime-dynamic.** `WorldRenderer.CascadeCount` is a property reading `GraphicsSettings.ShadowCascades`
(clamped to `[1, MaxCascadeCount]`) — the shadow-quality knob; fewer cascades = fewer depth passes = more FPS at
lower far-shadow resolution. The depth-pass loop runs to it, and the composition gets it as `uCascadeCount` and
loops/indexes to it. The fixed scratch (`_lightViewProj`/`_shadowFrusta`/`_cascadeSplitsView`/…) and the shadow
framebuffer's `Texture2DArray` layers are sized to **`MaxCascadeCount`** (= `ShadowFramebuffer.MaxCascadeCount`,
= shader `MAX_CASCADES`), so changing the count needs **no realloc and no shader recompile**. **Only `MaxCascadeCount`
(C#) and `MAX_CASCADES` (`Composition.fs`) must stay in sync** (bumping it also needs more `CascadeColor` entries);
the active count is free to vary at runtime.

**Occlusion culling — per-chunk visibility graph + a camera-chunk BFS ("Minecraft cave culling").** The
geometry pass does **not** linearly scan every loaded chunk; it draws only what a sight-line from the camera
can actually reach. Two halves:

- *Connectivity graph (mesh thread, `ChunkRenderData.ComputeConnectivity`).* At remesh time a 16³
  connected-component flood over the chunk's **see-through** cells (air, or any non-`IsOpaqueFullBlock` —
  glass/torches/slabs pass) records which of the 6 chunk faces are mutually reachable: an `int` of **15
  unordered face-pair bits** (faces `0=-X 1=+X 2=-Y 3=+Y 4=-Z 5=+Z`, `opposite=f^1`; two faces connect iff
  one see-through component touches both). Fast paths: an all-air chunk (`IsEmpty`) is `AllConnected`, a
  fully-solid chunk connects nothing — together almost every chunk in the thin-heightmap world. Reused
  static flood buffers (mesh-thread-only) → zero per-remesh alloc. `ChunkRenderData.PairBit` is the **single
  source of truth** for the bit layout (producer + BFS consumer both go through it).
- *Visibility BFS (main thread, `WorldRenderer.BuildVisibleSetBfs`).* A breadth-first flood from the camera
  chunk that **cannot cross a solid chunk**: a node carries the face it was entered through, and may only
  exit faces the entered face connects to in that chunk's graph (plus the camera's own chunk and any
  missing/not-yet-meshed chunk are **passthrough**, so an unstreamed air gap never breaks connectivity).
  **Visited is keyed on chunk position only and there is no accumulated direction mask**, so the reached set
  is a strict **superset** of the truly-visible set — it can over-draw (e.g. around an L/U-bend cave) but
  **never hides** visible geometry.
  - **Traversal is frustum- + render-distance-culled** (the camera's own chunk is **exempt** from the frustum
    test, else the root could be culled — its centre can sit behind the near plane — and the whole BFS dies =
    black screen). Frustum-gating keeps the flood bounded to the view cone (~hundreds of `visited`); without it
    the BFS floods the entire air-connected sphere — looking at a half-sky vista that was **~11 k visited**
    (vs ~2 k for the linear scan) and a **net −10 fps**, because it did 5× the visibility work to discover an
    open vista has nothing to occlude. Frustum-gating is **still correct** for genuinely-visible chunks: the
    frustum is convex with the camera at its apex, so a straight unobstructed sight-line to a visible chunk
    stays inside it; every chunk the ray crosses passes the (conservative bounding-sphere) frustum test and
    connects entry-face→exit-face through the air the ray travels, so the BFS reaches it. It can only drop
    chunks reachable *solely* via a bent, occluded path — which are not visible anyway. (The earlier
    render-distance-only variant was an over-correction; the popping it chased was the root-cull bug, fixed by
    the camera-chunk exemption.)
  - BFS order is ~front-to-back (free early-Z). **On an open surface vista almost nothing is occluded, so the
    cull legitimately draws nearly everything in frustum** (≈ the linear scan, e.g. 480 vs 510) — correct, not
    a bug; and since the saved draws are mostly *empty-mesh* buried chunks (the mesher emits only air-exposed
    faces), the GPU win there is near-zero. The cull pays off underground / behind walls / valley-into-hillside,
    where most of the frustum is solid-blocked. The dominant surface GPU cost is the **shadow passes**, which
    occlusion can't reduce (a directional light's casters aren't camera-occluded). **F5 toggles it off**
    (`BuildVisibleSetLinear`) for A/B; watch the F3 `drawn / total`.

**The visible set gates the shadow passes.** `BuildVisibleSet` sets `_anyShadowReceiver` iff a visible chunk
within `ShadowDistance` is **sky-exposed** (`ChunkRenderData.SkyExposed = Chunk.HasAnySkyLight()`), and the
`CascadeCount` cascade depth passes run only when `sunUp && _anyShadowReceiver && GraphicsSettings.Shadows` (the last is
the user's **Shadows** graphics option — see the state-system section). Deep in a cave nothing visible is
sky-lit, so the passes (a fixed per-frame GPU cost) are skipped entirely; look out a cave mouth and the BFS
reaches the sunlit terrain, so they run again. The sun is a *directional* viewer, so this can only decide
**whether to run the passes**, not prune casters — `DrawShadowMap` keeps its per-cascade light-frustum caster
cull. When the passes are skipped the **stale** shadow map is left bound; it is never sampled, because the
composition already early-outs shadow sampling exactly where `_anyShadowReceiver` is false (sky-occluded
`uLight.a≈0`, past `ShadowDistance`, or `uSunFade≈0`) **or where the Shadows option is off** (`uShadowsEnabled=0`,
which forces `shadow=1` so a sky-lit surface stays fully sun-lit instead of sampling the stale map). If a cave-mouth artifact ever appears, the follow-up
is a `uShadowValid` uniform forcing those pixels lit; not needed so far.

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
                                    ▲ │ Options                              │ Esc
                                    │ ▼                                      ▼
                                    │ GuiGraphicsOptions (overlay) ◀── Options ── GuiPauseMenu (overlay)
                                    └────────── Save & Quit ◀──────────────────┘
```

`StateWorld(window, multiplayer)` builds the connection (loopback+integrated `WorldServer` for SP, or a
`TcpConnection` for MP), creates the `WorldClient`, and logs in. On a failed MP connect it flips back to
the main menu.

**Graphics options.** `GuiGraphicsOptions` (reachable from both `GuiMainMenu` and the `GuiPauseMenu` overlay
via their "Options" button) is an **overlay** — it draws over whichever screen opened it and closing it
(Done / Esc) reveals that screen again, so opening options from the pause menu doesn't tear down the world.
Each button cycles a value in `GraphicsSettings` (`MinecraftClone3API/Client/GraphicsSettings.cs`), a static
holder persisted to `GamePaths.GraphicsSettingsFile` (`GraphicsSettings.json`): **VSync** (Off/On/Adaptive),
**Shadows** (On/Off), **Fullscreen** (On/Off). The setters write the file and push window-level state onto the
live `ClientResources.Window` (`VSync`, `WindowState`); `Shadows` has no window state — it gates the shadow
passes + `uShadowsEnabled` in the renderer (see the rendering section). `Program.Main` calls
`GraphicsSettings.Load()` before creating the window and seeds `NativeWindowSettings` from it, so the window
opens with the saved vsync/fullscreen choice.

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

In-world keys (handled in `PlayerController.Update`). The toggles + per-frame stats live in
`Client/Graphics/RenderDebug.cs` (a single static, **not** scattered on `WorldRenderer`): `PlayerController`
flips the toggles, `WorldRenderer` reads them and writes the stats, `StateWorld.Render` draws the F1/F3
overlays, and `GameClient` writes the frame timings. F4 chunk borders are the one exception (kept on
`ChunkBorderRenderer.Enabled`, their own renderer).

- **F1** — toggle the controls/help overlay: a fixed keybind list (movement, mouse, break/place, the
  debug keys) drawn top-left. `RenderDebug.ShowControls`, drawn in `StateWorld.DrawControls`.
- **F3** — toggle the on-screen diagnostics overlay: FPS + smoothed frame ms, `gpu`/`cpu upd` ms, **chunks
  drawn / total + visited** (the occlusion-cull readout — drawn drops far below total in a cave; `visited`
  is the BFS reach), shadows on/off + cascade count, loaded/mesh/upload queue depths, player pos + chunk.
  `RenderDebug.ShowDiagnostics`, drawn in `StateWorld.DrawDiagnostics` from `RenderDebug` fields
  (`DrawnChunks`/`VisitedChunks`/`ShadowPass` written by `WorldRenderer`; `FrameMs`/`GpuMs`/`UpdateMs` by
  `GameClient`). This is the cheap, always-available live HUD; the CSV profiler (F10) is the heavy tool.
- **F4** — toggle chunk-border wireframes around the player (current chunk red, neighbours yellow).
  Code: `Client/Graphics/ChunkBorderRenderer.cs`, drawn in the geometry pass (depth-tested).
- **F5** — toggle occlusion culling **off** (default on): falls back to the linear all-chunks scan
  (`BuildVisibleSetLinear`) and forces the shadow passes on whenever the sun is up, so visible geometry ON
  vs OFF should be identical (only draw order + drawn-chunk count differ — anything that appears only with it
  OFF is a hidden-geometry bug; watch the F3 `drawn / total` readout for the win). `RenderDebug.DisableOcclusionCulling`.
- **F6** — toggle cascade tinting: the composition tints each lit pixel by which cascade it samples
  (green→yellow→orange→red, near→far; grey past the shadow distance), so the CSM split is visible directly
  on the terrain. (A world-space wireframe of the cascade volumes was tried and dropped: the cascades are
  the *camera's own* frustum slices, so drawn from the camera they collapse to a screen-border rectangle —
  on-terrain tinting is the readable view.) `RenderDebug.CascadeTint` → `uDebugCascade`.
- **F7** — toggle the raw shadow-factor view: composition outputs the per-pixel shadow term as greyscale
  (white = lit, black = shadowed), isolating the shadow test from lighting to spot acne/peter-panning/bad
  cascades. `RenderDebug.ShadowFactor` → `uDebugShadow` (`Composition.fs`).
- **F10** — toggle the frame profiler. Writes a CSV per render frame to
  `~/.local/share/MinecraftClone3/profiling.csv` (`GamePaths.UserDataDir`). An on-screen `● REC`
  shows while recording; toggling off (or leaving the world) flushes and closes the file.
  Columns: `t, frameMs, fps, updateMs, renderMs, swapMs, gapMs, gpuMs,
  shadowMs/geomMs/compMs (per-pass GPU time, see below), updCalls, gen0/1/2 (cumulative),
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
  returned instantly); `gpuMs` = actual GPU render time (`GL_TIME_ELAPSED` query). `gpuMs` large ⇒ GPU-bound;
  `gpuMs` small but `gapMs` large ⇒ present/event overhead, not the GPU. `shadowMs`/`geomMs`/`compMs` split
  `gpuMs` into the three deferred passes — the cascaded shadow depth passes, the G-buffer geometry pass, and
  the fullscreen composition (PCF) — via `GL_TIMESTAMP` marker queries (`Client/Graphics/GpuTimers.cs`,
  populated only while recording, so a normal run issues no extra GL; the markers coexist with the whole-frame
  `GL_TIME_ELAPSED` because timestamps don't nest). **Both the whole-frame and the per-pass timers read back
  from a query ring harvested newest-ready, NOT a 1-frame ping-pong:** with vsync **off** the CPU runs several
  frames ahead of the GPU, so last frame's query usually isn't finished when read — a 1-frame read would
  perpetually see "not available" and freeze `gpuMs`/`shadowMs`/`geomMs`/`compMs` at a stale constant (this
  actually happened: a vsync-off capture pinned `gpuMs` at a single value and all per-pass at 0). The ring
  gives the GPU many frames to finish; reads still never stall (only consume already-available results). They
  localize a GPU-bound frame: large `shadowMs` ⇒ the depth passes are geometry/draw-call-bound
  (the usual culprit — the far cascade redraws most of the world; see the shadows note), large `compMs` ⇒ the
  composition is fill/shader-bound (the 12-tap PCF, which the per-draw/triangle view of a RenderDoc capture
  can't see). Their sum is **< `gpuMs`** (the remainder is GUI + clears + present setup); a skipped shadow
  pass logs `shadowMs = 0`. ⚠️ With **vsync on**, the composition pass (the first to write the default
  framebuffer) absorbs swapchain back-pressure, so `compMs` reads a vsync-quantized stall, not real fill —
  read `compMs` only from a **vsync-off** capture. When `updateMs` is large instead, srvMs/netMs/cliMs and their sub-splits localize it — e.g.
  `upMs ≈ updateMs` ⇒ the GL upload (re-`BufferData` of edited chunks) is stalling, not lighting/meshing
  (which are off-thread). `updCalls` is `OnUpdateFrame` calls per render frame (OpenTK fixed-timestep
  catch-up); ≫ 1 means updates are running behind and being batched.
  The profiler reads only lock-free mirrors (`WorldClient.LoadedChunkCount`/`MeshQueueDepth`/`UploadQueueDepth`,
  maintained on the writing threads) — **never** `ConcurrentDictionary.Count` (all-stripe lock) or a
  `_meshLock` take — so recording with F10 on doesn't contend with the apply/mesh threads and inflate the
  very stutter it measures.
  Code: `Util/Profiler.cs`, fed from `GameClient.OnRenderFrame` + `StateWorld.Update` (phase times).

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

**GPU frame capture (RenderDoc).** For per-draw GPU timing, overdraw, shader and texture/buffer
inspection of the deferred passes, capture a frame with RenderDoc. RenderDoc only hooks our GL context
over **GLX (X11)** — under native Wayland GLFW 3.4 makes an EGL context it reports as "unknown window"
and can't capture (and `WAYLAND_DISPLAY` can't be unset to escape Wayland: `wl_display_connect` defaults
to the `wayland-0` socket). So `Program.Main` forces the GLFW X11 backend (`InitHintPlatform.Platform`)
when launched under RenderDoc (auto-detected via `RENDERDOC_CAPOPTS`) or when `MC3_FORCE_X11=1`; normal
runs keep native Wayland. Build first, then launch the **native apphost** (not `dotnet run`, which
forks):

```
renderdoccmd capture -d <bin/Debug/net10.0> <bin/Debug/net10.0/MinecraftClone3>   # F12 in-world to snap
```

`GraphicsDebug` (`Client/Graphics/GraphicsDebug.cs`) emits `KHR_debug` groups + object labels so a capture
is navigable: the passes nest as **Shadow → Cascade 0..3 / Geometry → Opaque/Transparent/Overlays /
Composition** (RenderDoc shows per-group GPU time — the per-pass breakdown for free, no in-engine timing),
and the G-buffer/shadow targets and shader programs get names. Every call is a no-op unless `Enabled`
(same `RENDERDOC_CAPOPTS`/`MC3_FORCE_X11` detection, or `MC3_GL_DEBUG=1`), so normal runs and macOS (no
`KHR_debug`) never touch the entry points. **A depth-only pass being the GPU bottleneck means
geometry/draw-call-bound, not fill/shader-bound** — a 2026-06-20 capture showed the shadow pass at
1333 draws (cascade 3 alone 994) vs 470 for the visible scene; the fix is reducing geometry submitted to
the shadow pass (occlusion culling, staggered far cascades), not shader work.

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
  set). Paletted storage shrinks both ~10–50× — uniform/near-uniform chunks and the single-value light/sky
  containers (all-zero block light away from torches; all-zero or all-15 sky away from the surface) cost
  almost nothing. The flat `Chunk.Index(x,y,z) = (x*16+y)*16+z` ordering survives as the
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
  `Codepoints` iterator is kept only for the one-time load paths. `Profiler.Record` (active only under F10, but
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
  scan) — and the `BuildVisibleSetLinear` fallback (F5) + the shadow caster cull iterate it by index
  (contiguous, cache-friendly, zero allocation). The default occlusion BFS (`BuildVisibleSetBfs`) instead does
  lock-free **by-position `RenderData.TryGetValue` lookups** (never an enumeration), so the no-enumeration
  property holds there too. `RenderData` stays a `ConcurrentDictionary` purely for those by-position lookups
  (`DrainRenderReady`, the upload loop, the mesh thread's `TryGetValue`, the BFS, `UnloadChunk`). The
  profiler's `renderData` column now reads
  `RenderList.Count` (a field read) instead of `RenderData.Count` (which acquires **all** of the
  dictionary's locks) — that `.Count` ran every frame in `Profiler.Record`, *after* the `renderMs`
  stopwatch stops, so it landed in `frameMs` but not `renderMs`; with F10 on (the maintainer profiles with
  it on) that was unmeasured main-thread overhead.

## Known rough edges / deferred work

- **Sun shadows are cascaded but capped at `ShadowDistance` (160).** Coverage now reaches `ShadowDistance`
  via the active `CascadeCount` (**default 2**, up to 4) cascades and fades out at the edge, but geometry past
  it (out to the 256-unit render distance) still has no sun shadows, and far cascades are intentionally
  low-resolution (and the **default favours low-res/soft shadows** — small `ShadowMapSize`, few cascades — as an
  art-direction choice, not a cap). Raising
  `ShadowDistance`/`CascadeCount`/`ShadowMapSize` improves coverage/sharpness but each costs an extra opaque
  depth pass (the far cascade's light frustum is large, so it redraws roughly as much geometry as the main
  pass — this is the dominant new GPU cost; tune for the target GPU). Going further would mean more cascades
  or a distorted single map; deferred. Bias is a scene/driver-dependent tradeoff (per-cascade normal-offset
  + polygon-offset + depth bias): too little ⇒ acne, too much ⇒ peter-panning — the constants (`NormalBias`/
  `DepthBias` in `ShadowResolve.fs`, `ShadowStrength`/`ShadowSoftness`/`CascadeSplitLambda`/`ShadowCasterExtent`/
  `GL.PolygonOffset` in `WorldRenderer`) may need a pass on the target GPU (Mesa). **`MaxCascadeCount` (C#) and
  `MAX_CASCADES` (`ShadowResolve.fs` + `Composition.fs`) must be changed together** (the active `CascadeCount`
  is a runtime setting, `1..Max`), as must `ShadowMapSize` (C#) and the `ShadowTexel` constant (`ShadowResolve.fs`).
- **The shadow depth passes are a fixed per-frame cost (resolution-independent) — except they're now skipped
  in caves.** The cascade passes (default 2) redraw opaque geometry from the sun's POV every frame regardless of
  window size, so they hit hardest at *low* framerate headroom, not specifically at fullscreen (the fullscreen
  drop is the resolution-scaled G-buffer + composition fill; the composition early-outs above target that).
  They are **gated on `_anyShadowReceiver`** (a visible sky-exposed chunk within `ShadowDistance`, set by the
  occlusion BFS — see the rendering section), so deep in a cave they're skipped entirely; above ground they
  still run every frame and can't be skipped/cached there because the sun moves every frame (all cascade
  matrices change). The future lever is **staggered
  cascade updates**: re-render far cascades every N frames with a slightly stale sun (cheap — they're soft,
  far, and the sun moves ~1.5°/s) while near cascades update every frame; needs per-cascade cached matrices
  so the stored depth still matches what composition samples. Lowering `CascadeCount` or `ShadowMapSize` is the
  immediate knob and the **default already takes it** (2 cascades, 1024 map — the soft-shadow art direction);
  raise either for sharper shadows at more passes. Staggered far-cascade updates remain deferred.
- **No texel snapping → mild shadow shimmer while the camera moves.** Because the sun moves every frame,
  the shadow projection is intentionally unsnapped (snapping would flicker — see the shadows section), so a
  fixed-grid stabilization isn't available. The cost is faint sub-texel edge crawl while *walking/turning*;
  PCF softens it and the sun's own crawl masks it. If the day cycle were ever paused or made very slow,
  re-introducing texel snapping (snap the cascade centre to its light-space texel grid) would be worth it.
  `DayLengthSeconds` (240) sets how fast the sun — and thus the shadows — move.
- **Only opaque chunks cast sun shadows; entities cast/receive none.** Transparent geometry (water/glass)
  is excluded from the shadow pass (a solid black shadow from translucent material is wrong; a coloured
  shadow would need a transmittance pass). Remote players render "unlit" (`normal.w==1`, `BlockOutline`),
  so they neither receive shadows nor are added to `DrawShadowMap` to cast them. Both deferred.
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
  block light is 0 except near torches; sky is uniform 0 or 15 except at the surface and dug caves). No
  fallback-to-dense is implemented.
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
- **Sky light is "gen-seed + simple BFS" — known edit-time limitations (accepted scope).** At chunk-gen the
  sky container is seeded exactly from the heightmap (open-air cells = 15), so untouched terrain is perfectly
  lit. On edit, `UpdateSkyValues` floods sky like block light but with two simplifications: (1) **sky
  attenuates −1 in every direction including down**, so a cell reached only by downward *spread* dims with
  depth (note: a freshly-dug straight shaft does *not* dim — each dug cell re-seeds at 15 via `SkyExposed`'s
  straight-up scan; the dimming shows in caves/tunnels reached sideways, which is the desired dark-cave look);
  (2) **the equal-value removal ambiguity** — placing an opaque block to shadow a sky column won't cleanly go
  dark straight down, because a side-adjacent sky-15 cell back-fills it (15 − distance) in the removal BFS.
  Correct fix would be a persistent per-column heightmap + undimmed-vertical special-case (Minecraft's
  approach) — deliberately not done. `SkyExposed` is capped at `SkyScanMaxHeight` (256).
- **All-air chunks above terrain aren't streamed, so the client falls back to sky 15 for unloaded chunks.**
  An all-air chunk is `IsEmpty` and never added to `LoadedChunks`/streamed; its seeded sky never reaches the
  client. `WorldClient.GetSkyLight` returns `LightLevel.SkyMax` for any unloaded chunk (treat unloaded space
  as open sky) so surface tops still light up. Side effect: the bottom face of a block whose neighbour chunk
  is merely *not-yet-loaded* (not actually open sky) briefly samples 15 until that chunk streams in.
- **Day/night sun time is client-local (MP desync).** `WorldRenderer`'s clock advances in real time per
  client, so two MP clients see different times of day and the server has no authoritative time. SP is fine.
  Fix later via a server time packet. `DayLengthSeconds` (240) sets the cycle length.
- No movement interpolation for remote players (they snap to the last received position).
- `StateWorld` connects synchronously on the main thread; a far/unreachable MP host briefly blocks.
- `ClientSession.SentChunks` shrinks only on `ChunkRelease`/dirty resend, so a misbehaving or crashed client
  could leave stale entries until it disconnects. Bounded in practice by client `CacheDistance` eviction;
  fine for a hobby project, would need a server-side cap/timeout for hardening.

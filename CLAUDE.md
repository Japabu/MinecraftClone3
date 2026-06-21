# CLAUDE.md

> **⚠️ KEEP THIS FILE UP TO DATE.** This is the single source of truth for how the project fits
> together. Whenever you change architecture, threading, the wire protocol, the build/run flow, an
> invariant, or a convention, **update the relevant section in the same change** — a stale diagram is
> worse than none. If a task proves something here is wrong, fix it here too. **Prune as you go:** when a
> "Known rough edges / deferred work" item is resolved, delete it or move its rationale to a permanent
> section (e.g. "Performance notes") — that list is for *open* work only, not a changelog of past fixes.
> Treat editing this file as part of "done," not an afterthought.

A from-scratch Minecraft-like voxel engine in C# on OpenTK (OpenGL). Custom deferred renderer, plugin
system, chunked world with RGB block-light + sky-light propagation and a dynamic day/night cycle (procedural
skybox with a textured sun/moon, stars, sunset glow, and distance fog), a
**plugin-extensible world generator** (engine framework + vanilla content: biomes, ores, trees, caves,
oceans), **player movement** (walking with Minecraft-exact gravity/jump/AABB-collision plus a creative
free-flight toggle), and **client/server multiplayer** (singleplayer runs an in-process server over a
loopback connection; multiplayer connects to a dedicated server over TCP).

---

## Solution layout

```
MinecraftClone3.sln
├── MinecraftClone3API      Shared library — ALL engine logic lives here.
│   ├── Blocks/             WorldBase, WorldServer, Chunk (storage), CachedChunk, Block, LightLevel
│   ├── Client/             Client-only code (needs a GL context)
│   │   ├── Blocks/         WorldClient (client world replica)
│   │   ├── Graphics/       WorldRenderer, ChunkRenderData, EntityRenderer, BoundingBoxRenderer, VAOs, Camera
│   │   ├── GUI/            GuiBase, GuiButton, GuiSlider, widgets
│   │   └── StateSystem/    StateEngine, StateBase, GuiBase
│   ├── Entities/           Entity, EntityPlayer, PlayerController
│   ├── IO/                 GamePaths, WorldManager (+WorldInfo), FileSystem*, ResourceReader, CommonResources, plugin file systems
│   ├── Networking/         IConnection, Packet(s), Loopback/Tcp connections, ServerNetwork, ClientSession
│   ├── Plugins/            PluginManager, IPlugin, PluginContext
│   ├── WorldGen/           Dimension, Biome, Feature, Carver, BiomeSource, NoiseChunkGenerator, region, RNG
│   └── Util/               GameRegistry, BlockRegistry, ChunkMesher, WorldSerializer, WorldMetadata, OpenSimplexNoise, I18N
├── MinecraftClone3         Client executable (OpenTK GameWindow, 120 Hz). Owns Program + States/.
├── MinecraftClone3Server   Dedicated headless server executable (no GL).
└── VanillaPlugin           Content plugin: blocks (Stone, Sand, OakLog, Water, ores, ...) + the Overworld
                            dimension, biomes, ore/tree features (VanillaPlugin/WorldGen/).
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
connects there (`StateWorld.ServerAddress`). **Singleplayer worlds** each live in their own folder under
`~/.local/share/MinecraftClone3/Worlds/<name>/` (created/listed/deleted via the world-selection screen, see
the state section); the **dedicated server** uses one fixed `~/.local/share/MinecraftClone3/World/` (see
`GamePaths.WorldsDir`/`WorldDir`). Each world's name, generation seed, and last-played time are persisted to
`<worldDir>/level.dat` (`WorldMetadata`). **After a worldgen change, delete the affected world folder under
`Worlds/`** (and `World/` for the dedicated server) — chunks load disk-first, so old saves would otherwise
mask the new generator. Block textures come from a resource pack (a 1.13+ Minecraft client
jar) dropped in `~/.local/share/MinecraftClone3/ResourcePacks/`; with none, blocks render with placeholders.

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
  physics — the *client* runs walk gravity/collision and writes the result; the server just relays it). The
  client *requests* edits; the server applies and broadcasts the result.
- **Join handshake (loading screen).** Login → server assigns id + a seed-derived spawn (`LoginAccept`) and
  starts streaming chunks around it → once the server has *sent* the spawn column it sends **`PlayerReady`** →
  the client (`StateWorld._loading`) shows a loading screen, applies the spawn, and enters the world once
  `PlayerReady` arrives **and** the spawn chunks have decoded locally (so there's ground before gravity
  starts). This is one packet path, so SP (loopback) and MP (TCP) share the exact same flow; a wall-clock
  timeout only fail-safes a dropped signal. (No spawn torch — that was removed.)
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
     - 3 background threads                    - mesh-thread pool + 1 apply thread

   Chunk  (Blocks/Chunk.cs)            ChunkRenderData  (Client/Graphics/ChunkRenderData.cs)
   - PaletteStorage block ids          - holds a Chunk + a CPU opaque MeshBuffer (→ shared ChunkMeshArena)
   - PaletteStorage light (RGB)          + a per-chunk transparent SortedVertexArrayObject
   - PaletteStorage sky (0..15)        - Update() : CPU meshing (ChunkMesher) — safe off-thread
   - block data, min/max bounds        - TryUpload(arena) / DrawTransparent / Dispose : GL — main thread ONLY
   - Write(BinaryWriter)               - Update() gated on `Updated` (see invariants)
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
world. It meshes each of `Block.Model.Elements` (Minecraft `from`/`to` boxes with per-face `uv`/texture/
`cullface`), so partial-cube models (stairs) render as-is. **Per-block orientation** is `Block.GetModelTransform`
(default identity), composed after the element transform so it rotates the centred element about the block
origin — the engine parses **no blockstate files**, so a stair's facing (which vanilla keeps in the
blockstate) is applied here from the block's stored metadata. (Face normals + `cullface` are **not** rotated,
which is harmless: a partial block — `IsFullBlock` false — is never the both-full pair the face cull needs, so
its faces always draw; only flat shading on rotated faces is mildly off.) Chunk serialization (`Chunk.Write` ↔
`new CachedChunk(world, pos, reader)`) is reused for both disk
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

## World generation: an engine framework, vanilla owns the content

Generation is a **plugin-extensible framework in the engine** (`MinecraftClone3API/WorldGen/`) plus
**concrete content in `VanillaPlugin`** (the Overworld dimension, biomes, ores, trees). The engine provides
the machinery and reusable primitives; it bakes in **no** vanilla blocks or biome values. A third-party
plugin adds a biome or feature by registering one — no Vanilla edits.

```
            Dimension (RegistryEntry)            VanillaPlugin/WorldGen/
            - abstract CreateGenerator(seed)  ◀── OverworldDimension : Dimension
            - shared per-step feature lists       (registered "Vanilla:Overworld")
            - AddFeature(step, feature)           CreateGenerator wires a NoiseChunkGenerator
                     │
                     ▼ CreateGenerator(seed)
   IChunkGenerator ── NoiseChunkGenerator (the reusable noise generator)
   - Generate(CachedChunk, pos)        - SeaLevel=8, BedrockY=-32, WorldTop=96, MinChunkY=-2..MaxChunkY=6
   - Spawn(), Min/MaxChunkY            - seeded OpenSimplexNoise per field (continental/hills/peaks/temp/humidity)
        uses ▶ BiomeSource (ClimateBiomeSource: nearest biome in temp/humidity Voronoi)
        uses ▶ List<Carver>  (NoiseCaveCarver: 3D-noise spaghetti caves)
        uses ▶ Biome (climate point, surface blocks, height bias, per-step features)
        uses ▶ Feature (OreFeature, TreeFeature) via IChunkGenRegion + WorldGenRandom
```

**Registries.** `GameRegistry` holds `Registry<Biome>`/`Registry<Feature>`/`Registry<Dimension>` alongside
the block registries; `PluginContext.Register(Biome|Feature|Dimension)` prefixes with the plugin id exactly
like `Register(Block)`. All three are `RegistryEntry` (so they get `prefix:name` keys). Lifecycle: plugins
register **blocks → features → biomes → the dimension in `Load`**, then attach dimension-shared features
(the 4 ore veins) in **`PostLoad`** (so any other plugin's biomes/features already exist). `WorldServer` is
constructed *after* all `PostLoad`, so `Dimension.CreateGenerator` sees a complete registry — its
`ClimateBiomeSource` enumerates **every** registered biome tagged `Vanilla:Overworld`, so a plugin biome
auto-participates.

**Per-chunk pipeline (`NoiseChunkGenerator.Generate`, no neighbour block reads):**
1. **Biome + surface-height map** for the chunk's 16×16 columns (reused scratch). Biome is climate
   (temp/humidity Voronoi) for land, with **height-derived overrides**: base height well below sea → Ocean,
   shoreline band → Beach. Surface height = base noise + **blended** `HeightBias` + peaks·**blended**
   `HeightVariation`: `SurfaceHeight` bilinearly interpolates the four surrounding biomes' `HeightBias`/
   `HeightVariation` over a world-aligned lattice (spacing `HeightBlendSpacing`, smoothstep weights), so a
   biome border (e.g. Mountains↔Plains) is a **foothill, not a cliff**. The blend is a pure function of
   (wx,wz) — `_surf` holds the blended value and *all* height consumers use it: the fill writes it, the
   carver is handed the `_surf` array (so its carve ceiling is identically the filled surface — no skin
   breach), and trees/`Spawn` call the same `SurfaceHeight`. Surface *blocks* still snap to the hard
   per-column biome (`_colBiome`); only the *height* blends (Minecraft pre-1.18 behaviour).
2. **Base terrain** — bedrock at `BedrockY`, stone up to the surface.
3. **Surface skin** — biome `TopBlock`/`FillerBlock` above sea, `UnderwaterBlock` (sand/gravel) below.
4. **Water** — `Vanilla:Water` fills air below `SeaLevel` on ocean columns.
5. **Carvers** — `NoiseCaveCarver` overwrites stone with air below the surface skin (never bedrock).
6. **Sky seed** — open air above the surface → sky 15; the water column dims one level per block of depth
   (bright surface, dark deep); carved caves keep sky 0. Preserves the `IsEmpty` fast path (above-terrain
   air chunks only `SetSkyLight`, stay unstreamed; the client falls back to sky 15 for unloaded chunks).
7. **Decoration** — for the chunk and each origin in a **±1-chunk XZ margin**, seed a `WorldGenRandom` from
   `(seed, originChunk, feature.Salt)` and run the dimension's shared features (ores) then the origin's
   centre-biome features (trees) for each `DecorationStep` (Ores, Vegetation). Features emit in **absolute
   coordinates through `IChunkGenRegion`, which clips writes to the chunk being generated** — so a tree or
   vein straddling a border is computed identically by both chunks (Minecraft's population-seed model) with
   **no neighbour writes**. Decoration runs for every chunk the feature's Y-range can reach (it's gated by a
   local surface-height check), so a feature crossing a vertical chunk boundary is stamped consistently by
   both vertical chunks (the RNG is independent of chunk Y).

**Determinism & threading.** Seeds are **process-stable**: `OpenSimplexNoise` is a seeded instance (Fisher–
Yates perm from a SplitMix64 stream), `WorldGenRandom` is a struct SplitMix64 PRNG, and `Feature.Salt` is an
**FNV-1a** hash of the registry key (never `string.GetHashCode`, which is per-run randomized). `Generate`
runs only on the server **LoadThread** (single writer — Invariant 5 holds; the generator's column scratch is
a plain reused field). The **seed is persisted** to each world's `level.dat` (`WorldMetadata`, alongside its
name + last-played); both call sites construct `new WorldServer(long seed, string worldDir)` — `StateWorld`
singleplayer with the chosen `WorldInfo.Seed`/`Directory`, `MinecraftClone3Server` via
`WorldMetadata.LoadOrCreate(GamePaths.WorldDir, …)`. `WorldServer` owns a per-instance `WorldSerializer(worldDir)`
(its own dir + index caches), safe because exactly one `WorldServer` exists per process. The
generator resolves `GameRegistry.GetDimension("Vanilla:Overworld")`; if absent it logs and falls back to a
`FlatChunkGenerator` (empty void) so the engine still runs without Vanilla. **Generation is server-only** —
clients receive baked chunks, so there is no wire/client change (the new registries load client-side too but
are unused there).

**LoadThread band.** The interest scan loads the **full `MinChunkY..MaxChunkY` vertical column** within
`TerrainRadius` (10 chunks, matched to `ServerNetwork.ViewDistance`/`RenderDistance` = 160) around each
player — replacing the old thin surface slab, because the world now has real vertical extent (oceans, caves,
mountains). Per-tick cost is unchanged (16-chunk cap, distance sort, dedup); resident chunk count grows
(tune `TerrainRadius`/`ChunkLifetime`).

**Spawn** comes from `NoiseChunkGenerator.Spawn()` (spiral out from origin for the first land column);
`ServerNetwork` caches it and seeds `LoginAccept`.

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
  S→C  LoginAccept           assigns entity id + spawn (the client applies this spawn behind the loading screen)
  S→C  PlayerReady           spawn column streamed → client may apply spawn + enter the world (join handshake)
  S→C  ChunkData             Vector3i + Chunk (loopback: by ref; TCP: GZip of Chunk.Write)   (initial chunk streaming only)
  S→C  BlockChanges          ChunkPos + (localIndex, blockId, light, sky)[]   (edits + block-light + sky-light, see below)
  C→S  ChunkRelease          client dropped a chunk from its cache; clears its SentChunks entry
  C→S  PlaceBlockRequest     pos + block id (id 0 = break) + placement metadata (computed client-side, see below)
  C→S/S→C  EntityMove         own player up; relayed to others down
  S→C  EntitySpawn/EntityDespawn   remote players appearing/leaving
  S→C  WorldTime             world clock in seconds (TickCount·SecondsPerTick); on join + ~1/s, drives day/night
```

**Placement metadata is computed client-side, never read on the server.** `Block.OnPlaced(world, pos, player,
int metadata)` receives the metadata in the `PlaceBlockRequest`; the client derives it via
`Block.GetPlacementMetadata(KeyboardState, EntityPlayer, BlockRaytraceResult)` (default `0`) in
`PlayerController.PlaceBlock` and threads it through `WorldClient.PlaceBlock` into the packet. The player +
ray are passed so a block can orient by the placer's look and clicked face (stairs: facing from yaw, half
from the clicked face); `BlockTintedGlass` uses only the held-key tint. The headless server never touches
input — `ServerNetwork.ApplyPlaceRequest` just passes `place.Metadata` to `WorldServer.PlaceBlock` →
`OnPlaced`. (Like tinted glass, a stair's facing is block *data*, so it rides the whole-chunk `ChunkData`
resend, not the lighter `BlockChanges` delta.)

**Chunk caching & eviction is client-owned.** The client keeps every chunk it receives in memory and, each
`WorldClient.Update`, drops chunks whose centre is farther than `CacheDistance` (240) from the player,
sending a `ChunkRelease` for each. `CacheDistance` is kept comfortably above the server's send range
(`ServerNetwork.ViewDistance`, 160) so a freshly streamed chunk is never evicted-then-re-requested at the
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
sessions → **stream chunks** (nearest-first, in-range-not-yet-sent only, capped at `MaxChunksPerTick` per
session per tick — no unload pass) → **send `PlayerReady`** to any session whose `SentChunks` now covers the
spawn column (one-shot join signal, see the handshake above) → **flush block changes** (delta packets) →
**resend dirty chunks** (block-data only).

---

## Threading model  ⚠️ the load-bearing invariant

```
 WorldServer (background, started in ctor):
   LoadThread    disk load else _generator.Generate (world gen) over the full vertical band around each
                 player in PlayerEntities; fills _chunksReadyToAdd. Sole writer of generated chunks.
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
   MeshThread    a POOL of workers (Environment.ProcessorCount-2, ≥1), each draining the shared mesh queues →
                 ChunkRenderData.Update()  (CPU vertex lists only, NO GL; holds the VAO locks for the whole
                 remesh, so the main-thread upload must not block on them). Meshing was the load-fill
                 bottleneck (one thread pegged at 100%, chunks waiting ~99% of their latency in the queue; an
                 F10 chunk-trace measured 80→~350 chunks/s on the pool); it parallelizes because the
                 _meshPending claim (remove-under-_meshLock) gives each *queued* chunk to exactly ONE worker,
                 workers only READ chunk storage (Invariant 5 — concurrent readers ok), and GL stays on the
                 main thread (Invariant 1). Each worker also computes its chunk's SkyExposed flag (plain-field
                 idempotent write, read lock-free on the main thread by the shadow gate — benign torn read
                 self-corrects next remesh). **One subtlety:** the _meshPending claim is per *queue-epoch*, not
                 per *instance* — if QueueMesh re-enqueues a position (an edit/light delta) while a worker is
                 mid-Update on it, a second worker can Update the SAME ChunkRenderData concurrently. This is
                 benign, NOT a bug: both Update calls serialize on lock(_vao)+lock(_transparentVao), each is a
                 complete self-contained remesh (its own pooled lists, read-only storage), and TryUpload's
                 Monitor.TryEnter never observes a half-built mesh — the only cost is a redundant remesh (it
                 cannot fire during the edit-free load burst)
   Update()      (MAIN thread) pumps packets (routing ChunkData/BlockChanges to _applyQueue, handling
                 entity/login inline), DrainRenderReady → creates ChunkRenderData (GL) + queues meshing,
                 TryUploads meshed chunks (GL, non-blocking, time-budgeted per frame — requeues a chunk being
                 remeshed), evicts, disposes
   RenderWorld() (MAIN thread) BuildVisibleSet runs the frustum + render-distance scan of RenderList, then
                 the shadow + geometry + composition passes

 Client game loop (MinecraftClone3/Program.cs, display rate ~120 Hz, MAIN thread):
   OnUpdateFrame → StateEngine.Update() → StateWorld.Update():
       per FRAME:  PlayerController.UpdateFrame (look/break/place/camera), world.Update() (GL + packet pump + evict)
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
so motion is smooth at the display rate even though the sim steps at 20 Hz. SP freezes the accumulator while
paused (unfocused); MP keeps ticking the (remote) server. `UpdateFrequency` stays at the display rate — only
the sim cadence is fixed. (`ServerNetwork.MaxChunksPerTick` was sized up ~6× when the streaming loop went
from the ~120 Hz frame rate to 20 tps, to hold the same chunks/second.)

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
    └─ BuildVisibleSet → frustum + render-distance scan of every loaded chunk; fills the
         opaque/transparent draw lists and flags _anyShadowReceiver (a visible sky-exposed chunk)
    └─ DrawShadowMap (only while the sun is up AND _anyShadowReceiver) → ShadowFramebuffer (depth-only)
         one depth map (Texture2D); opaque chunks re-drawn from the sun's orthographic POV
         (ShadowDepth shader, no colour output)
    └─ DrawGeometryFramebuffer → GeometryFramebuffer (MRT G-buffer)
         attachment 0: diffuse   1: normal (w=1 ⇒ "unlit, pass through")
         2: RGBA8 light (rgb = baked block light, a = baked sky-light factor 0..1)   + depth
         · opaque chunks front-to-back via ONE batched multidraw (shared ChunkMeshArena), then
           transparent back-to-front (per-chunk sorted VAO)
         · EntityRenderer  : remote players as solid placeholder cubes (BlockOutline shader, unlit)
         · PlayerController : block-targeting outline
    └─ DrawShadowResolve (same gate as DrawShadowMap) → ShadowResolveFramebuffer (HALF-res RGBA8)
         the 12-tap PCF runs here at half res (quarter the pixels): r = shadow factor, g = norm
         view depth; reads G-buffer normal/depth/light + the shadow depth map
    └─ DrawComposition → screen
         background pixels (cleared far plane, viewDepth ≥ uSkyDistance) ⇒ SkyColor(viewRay) — the skybox;
         shadow = depth-aware (joint-bilateral) upsample of the half-res resolve buffer (1=lit, early-outed
         past ShadowDistance / in caves / at night); skyLight = sky.a*(shadow*uSunColor*uSunFade + uSkyAmbient);
         light = max(blockLight.rgb, skyLight); lit = diffuse * max(light, MinLight); water reflects SkyColor;
         lit fades into uHorizonColor with distance fog; normal.w==1 ⇒ diffuse unlit
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
sun/moon *colour/intensity* animate). The clock is **server-authoritative**: `WorldRenderer` reads
`WorldClient.WorldTimeSeconds` (synced from the periodic `WorldTime` packet — the server's
`TickCount·SecondsPerTick` — and advanced locally between packets), so all MP clients share one time of day.
**Moonlight is non-directional ambient** (no moon shadow pass) — deferred.

**Background sky (the skybox) — procedural, in `Composition.fs`, no geometry.** Background pixels are the
*cleared far plane*: `main()` reconstructs each pixel's view-space depth and, when it is `≥ uSkyDistance`
(`WorldRenderer.RenderDistance + 48`, comfortably past the farthest drawn chunk yet inside the 512 far clip
plane), reuses the reconstructed far-plane point as the view-ray direction and shades `SkyColor(dir)` instead
of a G-buffer lookup — so the sky costs nothing but the fullscreen pass that already runs. `SkyColor` builds a
Minecraft-style sky from `WorldRenderer`-computed time-of-day uniforms: a vertical gradient (`uVoidColor`
below the horizon → `uHorizonColor` haze → `uSkyColor` zenith), a sunrise/sunset orange band near the horizon
in the sun's azimuth (`uSunsetColor`), procedural **stars** (a hashed direction grid, faded in by
`uStarBrightness` at night and out toward the horizon), and a **textured sun and moon** drawn as angular
billboards (`CelestialBillboard` projects the view ray onto a tangent plane at the body's direction → quad
uv). The sun/moon textures are the real pack assets — `minecraft/textures/environment/sun.png` and
`moon_phases.png` (full-moon cell), loaded by `WorldRenderer.LoadSkyTextures()` after the resource packs are
indexed (alongside `Font.Load`, see resource loading) onto composition units 5/6; with **no pack** the
`uHasSunTexture`/`uHasMoonTexture` flags are 0 and the shader falls back to a procedural disc. The moon sits
opposite the sun (`-uSunDirection`), each hidden below its own horizon. **Water reflects this same `SkyColor`**
(see the water section), so the sun glints and the moon/stars reflect at night — one sky function, two
consumers. **Distance fog** then melts lit geometry into `uHorizonColor` between `uFogStart`/`uFogEnd`
(0.72–0.97 × `RenderDistance`), hiding the hard chunk-load boundary against the sky (and fading to night
darkness, since the horizon colour itself dims). The sky/sun/star/fog colours are all `WorldRenderer` C#
functions (`SkyZenithColor`/`SkyHorizonColor`/`SkyVoidColor`/`SunsetColor`/`StarBrightness`, sharing
`DayTime`/`SunHeight`/`DayFactor` with the existing `SunColor`/`SkyAmbient`/`SunDirection`), so retuning needs
no shader recompile; the billboard sizes are the `SunSize`/`MoonSize` consts.

**Directional sun shadows — one low-res shadow map (no cascades).** `DrawShadowMap` renders a **single**
orthographic depth map into `ShadowFramebuffer` — one `Texture2D` of `ShadowFramebuffer.ShadowMapSize` (1024).
**The map is deliberately low-res for a soft, blurry look:** the PCF penumbra is a fixed number of *texels*, so
a coarse world-per-texel (one 1024 map covering the whole shadow distance) reads as a wide, soft world-space
penumbra — soft instead of razor-sharp. Bump `ShadowMapSize` (and the `ShadowTexel` constant in
`ShadowResolve.fs`) for sharper shadows, or shorten `ShadowDistance` for finer texels over less ground; the
low-res default is an art-direction choice, not a limitation. (CSM was removed — a single map is the simple,
sufficient choice for a player-centred voxel world; cascades only buy crisp near-shadows the project doesn't
want.) The map is fit to the **analytic bounding sphere** of the
`[WorldRenderer.ShadowNear, WorldRenderer.ShadowDistance]` (160) view-frustum slice: the centre rides the
camera forward axis and the **radius depends only on near/far + FOV (read from the projection matrix), so it is
constant as the camera rotates → no size shimmer**. The projection is **deliberately NOT texel-snapped**: the
sun advances every frame (day/night cycle), and snapping to the *rotating* light-space texel grid quantizes the
shadow's smooth crawl into ~20 Hz whole-texel jumps — the visible flicker. Unsnapped, the projection follows the
sun smoothly and the soft low-res PCF keeps camera-motion shimmer down. (Texel snapping is a best practice for a
*static* sun + moving camera; a fast moving sun inverts the trade — see "Known rough edges".) The sun direction
comes from `WorldRenderer.SunDirection()` (same `_dayClock` as the colour, so the brightest sun is the highest
sun). The pass re-draws the **already-uploaded opaque chunk VAOs** (no remesh) with the trivial `ShadowDepth`
shader, frustum-culling chunks against the light frustum; `PolygonOffset` + a normal-offset (scaled by the
map's world-units-per-texel, `_shadowTexelWorld`) + a small depth bias fight self-shadow acne (culling is
**off** — the voxel mesher emits only single-sided exposed faces). The depth map is a **hardware shadow
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
view depth (for the upsample). `ShadowResolve.fs` does the world-pos reconstruction (`uViewProjectionInv`),
light-space projection (`uLightViewProj`) and a **10 %-of-`ShadowDistance` far fade** (no hard edge).
Composition then **depth-aware (joint-bilateral) upsamples** the half-res buffer back to full
res: a 2×2 tap weighted by bilinear position **and** by how close each tap's stored depth (`g`) is to the
pixel's (`exp(-|Δdepth|·DepthSharpness)`), so the half-res shadow doesn't bleed across silhouette edges
(`DepthSharpness` in `Composition.fs` is the tunable edge-vs-blockiness knob). The result is the same 0..1
`shadow` factor multiplying **only** the direct sun part of the sky term — ambient sky fill (`uSkyAmbient`) and
block light untouched, so a *sky-exposed* shadow falls back to the blue sky fill (not black), a *sky-occluded*
one (a cave) goes dark.
Both the resolve and the upsample are **early-outed** where the sun term can't matter — past the shadow
distance (`viewDepth ≥ ShadowDistance`), sky-occluded (`uLight.a ≈ 0`, caves/interiors), or at
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

**Visible set — a linear frustum + render-distance scan (no occlusion culling).** `BuildVisibleSet` iterates
`WorldClient.RenderList` (a plain main-thread `List<ChunkRenderData>` mirroring the `RenderData` dictionary's
values — see the performance note), keeps each chunk whose bounding sphere is in the view frustum and within
`RenderDistance`, and buckets it into the opaque / near-transparent / far-transparent draw lists. The mesher
emits only air-exposed faces, so a buried chunk's mesh is near-empty and costs almost nothing to submit; the
dominant surface GPU cost is the shadow pass + composition fill, not buried-chunk draws. (A per-chunk
visibility-graph BFS "cave cull" was tried and removed — it over-drew on open vistas, didn't reliably win in
caves, and the connectivity graph + BFS were complexity the simple scan doesn't need.)

**Opaque + shadow geometry is batched through one shared vertex arena (`ChunkMeshArena`).** Every chunk's
**opaque** mesh is built (mesh thread) into a CPU `MeshBuffer` and uploaded (main thread) into a sub-range of a
shared set of GL buffers — 5 vertex VBOs + 1 index buffer — managed by a coalescing first-fit `RangeAllocator`
(grows by reallocating + `glCopyBufferSubData`). Positions are **baked world-space at mesh time**
(`ChunkMesher` adds `chunk.Position*16`), so no per-chunk model matrix is needed (`uWorld` was dropped from
`WorldGeometry.vs`/`ShadowDepth.vs`); that's the precondition that lets many chunks share one buffer. The whole
visible opaque set then draws with **one `glMultiDrawElementsBaseVertex`** (GL 3.2 core, the 4.1 cap allows it)
per pass — geometry uses the full-attribute VAO, the shadow depth pass a **position-only VAO** over the same
buffers. Per-chunk indices are 0-relative; the arena passes each chunk's vertex-range start as the sub-draw's
`baseVertex`. **Transparent meshes stay per-chunk** (`SortedVertexArrayObject`) — they need an independent
per-frame back-to-front index sort. The arena is touched main-thread only (upload in the client upload loop,
free in its dispose drain), so no locking; GL stays on the main thread (Invariant 1). **Measured effect:** this
**halves CPU draw submission** (`renderMs` ~3.7→~2.0 ms — one bind + one multidraw call instead of ~238
bind+uniform+draw triples per pass) but **does NOT reduce GPU time** on Mesa, because non-indirect
`glMultiDrawElements` is a driver-side CPU loop (the GPU still sees N sub-draws). It's kept for the CPU win
(helps the CPU-bound streaming case) + future GL 4.3 indirect-draw + cleaner architecture; see the performance
findings below for why the GPU, not the CPU, is the steady-state wall.

**Distance LOD — coarse meshing of far chunks (the high-render-distance lever).** The geometry pass is
triangle/primitive-setup bound (see findings), so at high render distance the per-frame triangle count is the
wall. Distant chunks (where the detail is sub-pixel) are re-meshed at a coarser stride: **LOD 0** = full
per-block (`< 96` blocks), **LOD 1** = stride-2 (`< 160`), **LOD 2** = stride-4 (beyond). `ChunkMesher.
AddBlocksToVaoLod` samples each stride³ region at its corner block and emits one scaled face per exposed
super-block direction (cull = the neighbour super-block's corner sample — approximate but fine at distance),
all into the opaque arena (no separate transparent pass for LOD chunks). `ChunkRenderData.DesiredLod` is set
by `WorldClient.ScanLod` (a distance scan gated on chunk-border crossings, like eviction; **bidirectional** —
re-meshes on both approach *and* recede, since a recede chunk can rotate back into view; low-priority
re-meshes so they sit behind first-load streaming) and `DrainRenderReady` (initial level at stream time); the
mesh thread reads it in `Update`. **This took `geomMs` 4.8→1.2 ms at RD 16** and is the change that makes
500 FPS reachable at RD 16. Tradeoff: distant terrain coarsens (near stays full detail); thresholds are the
`Lod1DistanceSq`/`Lod2DistanceSq` consts.

**The visible set gates the shadow passes.** `BuildVisibleSet` sets `_anyShadowReceiver` iff a visible chunk
within `ShadowDistance` is **sky-exposed** (`ChunkRenderData.SkyExposed = Chunk.HasAnySkyLight()`), and the
shadow depth pass runs only when `sunUp && _anyShadowReceiver && GraphicsSettings.ShadowsEnabled` (the last is
the user's **Shadows** quality option ≠ Off — see the state-system section). Deep in a cave nothing visible is
sky-lit, so the passes (a fixed per-frame GPU cost) are skipped entirely; look toward the surface and a
sky-exposed chunk comes into view, so they run again. The sun is a *directional* viewer, so this can only
decide **whether to run the passes**, not prune casters — `DrawShadowMap` keeps its light-frustum caster
cull. When the passes are skipped the **stale** shadow map is left bound; it is never sampled, because the
composition already early-outs shadow sampling exactly where `_anyShadowReceiver` is false (sky-occluded
`uLight.a≈0`, past `ShadowDistance`, or `uSunFade≈0`) **or where the Shadows option is off** (`uShadowsEnabled=0`,
which forces `shadow=1` so a sky-lit surface stays fully sun-lit instead of sampling the stale map).

**Water surface — Tier B (animated normals + Fresnel sky reflection + sun specular, no extra pass).** Water
is shaded specially **in `Composition.fs`** — deferred-correct, since composition already reconstructs world
position from depth and holds the sun/sky/shadow terms. No new render pass, framebuffer, or vertex attribute.
The chain: `BlockWater.GetRenderMaterial → RenderMaterial.Water` (an engine-level hint mirroring
`TransparencyType`); `ChunkMesher.AddFaceToVao` bakes that into the face **`normal.w = 0.5f`** (`WaterNormalW`),
which `EncodeNormal` stores as **0.75** (≈191/255 in Rgba8) in the G-buffer normal alpha — distinct from lit
solid (0.5) and the unlit flag (1.0). Composition detects a water pixel by a snug band around it
(`WaterFlagLo/Hi`, 0.7–0.8 — the flag is flat per-face and attachment 1 is written blend-off, so the stored
value is deterministic; a future `RenderMaterial` must encode outside this band) and, on top of the
existing Tier-A lit translucent water (`baseColor`), adds: animated **`WaveNormal`** (analytic gradient of
three summed directional sine waves over the surface's world XZ, scrolled by `uTime` — only the **top** face,
`faceN.y > 0.5`, is perturbed), a **Fresnel** mix toward **`SkyColor(reflect(-V, N))`** — the *same* sky
function the background skybox paints (see "Background sky"), so the water mirrors the real gradient, sun,
moon, and stars and tracks time of day — and a **Blinn-Phong sun specular** glint. New composition uniforms:
`uCameraPos`, `uSunDirection`, `uTime` (set in `DrawComposition`). Reflection and specular are scaled by the
baked sky factor (`uLight.a`) and `uSunFade` (and the glint by `shadow`), so cave/overhang water and night
water fall straight back to the plain look — the gating is free, no special cases. Look knobs are shader
`const`s (wave amp/freq/speed, `WaterF0`, `WaterSpecExp/Gain`); the reflected-sky look is shared with the
skybox uniforms.

The one subtlety this needs: composition must *identify* water pixels, but the transparent pass blends **all
three** MRT attachments, so a flag in `normal.w` would blend with the background and become unreadable. Fix:
during the transparent draw, **blend only attachment 0 (diffuse)** and disable blending on attachments **1
(normal)** and **2 (light)** via `GL.Disable(IndexedEnableCap.Blend, 1/2)`, restored to `Enable` right after
the pass. Diffuse still alpha-blends (translucency preserved); the front-most transparent surface writes its
normal + water-flag + light **cleanly** (overwrite, not blend). This needs **no `RenderState` change** — the
explicit restore keeps RenderState's single `Blend` bool the whole per-buffer description. Side effect
(intentional, arguably more correct): glass also writes its front pane's own normal/light instead of blending
them — diffuse translucency is unchanged, and it actually removes a latent `normal.w` corruption when glass
overlapped an unlit pixel. **`WorldGeometry.vs/.fs` are untouched** — the flag rides the existing
`EncodeNormal`, and water (input `w = 0.5`, not the unlit `1`) still samples its texture normally.

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
GuiResourceLoading ──(done)──▶ GuiMainMenu ──Multiplayer──────────────────────────▶ StateWorld(window, multiplayer:true)
                                    ▲ │ │ Options                                        │ Esc
                                    │ │ ▼                                                ▼
                                    │ │ GuiGraphicsOptions (overlay) ◀── Options ── GuiPauseMenu (overlay)
                                    │ └────────── Save & Quit ◀──────────────────────────┘
                                    │ Singleplayer
                                    ▼                  Create New World
                              GuiWorldSelection ◀──────────────────────▶ GuiCreateWorld
                                    │  │  ▲ Back/Esc                          │ Create / Cancel/Esc
                       Play/dbl-click│  │ Delete                             ▼
                                    ▼  │ (GuiConfirm overlay ── Yes ──▶ delete + rebuild)
                              StateWorld(window, world)  ◀──────────────────┘
```

`StateWorld` has two public ctors over a shared private one: `StateWorld(window, WorldInfo)` (singleplayer —
runs that world's folder in a loopback+integrated `WorldServer(seed, worldDir)`) and
`StateWorld(window, multiplayer)` (a `TcpConnection` for MP). It creates the `WorldClient` and logs in; on a
failed MP connect it flips back to the main menu. It then sits in a `_loading` phase (pumping
server/network/world + drawing a loading screen) until the join handshake completes (see "Join handshake" in
the networking section) before running player input.

**World selection (singleplayer).** The "Singleplayer" button opens `GuiWorldSelection` (lists the worlds
under `Worlds/`, sorted last-played-first via `WorldManager`), from which the player plays, creates, or
deletes a world. `GuiCreateWorld` takes a name + optional seed (blank → random, numeric → used directly,
else `WorldGenRandom.StableHash`) and `WorldManager.CreateWorld`s it. These are **states** (navigated by
`ReplaceState`), not overlays, because `GuiCreateWorld` owns `GuiTextInput`s that subscribe to the window's
`TextInput` event and must `Detach()` in `Exit()` — and `ReplaceState` calls `Exit()` on removed layers
while dead **overlays** are dropped *without* `Exit()` ([StateEngine.cs](MinecraftClone3API/Client/StateSystem/StateEngine.cs)).
The delete confirmation (`GuiConfirm`, a Yes/No) owns no text input, so it is a safe overlay.

**`GuiTextInput`** ([MinecraftClone3API/Client/GUI/GuiTextInput.cs](MinecraftClone3API/Client/GUI/GuiTextInput.cs))
is the reusable single-line text field: chars arrive via the window `TextInput` event (OS handles
layout/shift), a left click sets focus by whether it landed inside (so clicking elsewhere defocuses, no
cross-field coordination), Backspace deletes via `IsKeyPressed`. **Held-key repeat is not implemented** (one
char per key press); the owning state must call `Detach()` from `Exit()` to unsubscribe.

**Player movement & physics** (`Entities/PlayerController.cs` + `PlayerPhysics.cs`, client-only, main
thread). The player is a **0.6 × 1.8 AABB**; `Entity.Position` is the **feet** and the camera renders at
`Position + EyeOffset` (1.62) via `Entity.RenderPosition`/`EyeOffset` (defaults keep non-player entities a
point). `PlayerController` is split into `UpdateFrame` (per display frame: look, fly toggle, hotbar, debug
keys, break/place, camera) and `Tick` (one fixed **20 tps** step), driven by `StateWorld`'s accumulator (see
the game-loop section); `ApplyInterpolation(alpha)` lerps `PrevPosition→Position` so the 20 tps motion is
smooth at the frame rate. Two modes, toggled by **double-tapping Space**:
- **Walk (default):** exact-Minecraft constants integrated **once per 20 tps tick** —
  gravity `v_y=(v_y−0.08)·0.98`, jump `0.42`, ground accel `0.1`/friction `0.546`, air `0.02`/`0.91`, Ctrl
  sprint `1.3×`. `PlayerPhysics.MoveWithCollision` is **swept per-axis (Y→X→Z)**: it clips each axis's
  displacement against the collision boxes of overlapping solid blocks and zeroes the blocked component. A
  block contributes its boxes via **`Block.GetCollisionBoxes`** (block-local, centred ±0.5), which defaults
  to the single `GetBoundingBox` cube but lets a block return **several** boxes — stairs return an L (slab +
  step), so you can walk up them (each 0.5 rise is within auto-step). The clip loops iterate the boxes from a
  reused scratch list (no per-cell allocation). `GetBoundingBox` stays a single cube and is used for the
  *raytrace/targeting + outline* only, so targeting a stair is a simple whole-cube hit. `OnGround` is then a **velocity-independent
  downward probe** (`ClipY(box, −GroundProbe)` clipped ⇒ grounded), not the Y-clip outcome — so a tick that
  enters with `Velocity.Y==0` (spawn, just un-flew) or lands exactly flush doesn't read airborne for a tick.
  **Auto-step:** when grounded, **not rising** (`velY ≤ 0`), and a horizontal axis is blocked, the move is
  retried raised by `StepHeight` (0.6 = MC: up → horizontal → drop back down) and kept only if it advanced
  farther — climbs slabs/partial blocks, still needs a jump for a full cube. The not-rising gate matters: on
  the jump tick `velY = +0.42`, and stepping then would stack `StepHeight` on the jump's rise and clip the
  player straight up a full block — so stepping only happens while settling onto the ground, and the jump
  arc alone (apex ~1.25) decides whether a 1-block ledge is cleared.
- **Swim (in water):** when the body overlaps a `Block.IsLiquid` block (`PlayerPhysics.IsInLiquid` samples
  the lower + mid body), the walk tick takes the water branch instead — gentle water accel, all velocity
  damped by `WaterDrag`, **Space buoys up** (`SwimImpulse`), otherwise a slow sink (`WaterGravity` ≪ land
  gravity). Liquid is pass-through, so swept collision + the ground probe still run; you don't fall through.
- **Fly (creative):** the same fixed-step `Entity.Move` — Space/Shift up/down, Ctrl fast, **no
  gravity/collision** (noclip). Also runs in the 20 tps tick and is render-interpolated like walking.

The block-target raytrace uses the **eye** (`RenderPosition + EyeOffset`); `SendMove` ships the **feet**
position, so remote players (drawn by `EntityRenderer` as 0.6×1.8 boxes, offset up by half-height to stand
on their feet) line up. **Remote entities are render-interpolated:** their positions arrive at 20 tps, so
`Entity.SetInterpTarget` (on each `EntityMove`) aims a lerp from the current visual position toward the new
target, advanced per display frame by `WorldClient.UpdateEntityInterpolation`, and `Entity.RenderPosition`
returns the lerp — so they glide instead of snapping. (The local player overrides `RenderPosition` with its
own accumulator-driven interpolation.)

**Graphics options.** `GuiGraphicsOptions` (reachable from both `GuiMainMenu` and the `GuiPauseMenu` overlay
via their "Options" button) is an **overlay** — it draws over whichever screen opened it and closing it
(Done / Esc) reveals that screen again, so opening options from the pause menu doesn't tear down the world.
Each control mutates a value in `GraphicsSettings` (`MinecraftClone3API/Client/GraphicsSettings.cs`), a static
holder persisted to `GamePaths.GraphicsSettingsFile` (`GraphicsSettings.json`). The widget toolkit is two
elements: **`GuiButton`** (cycles a discrete value, relabelling itself) and **`GuiSlider`** (a drag slider for
a continuous/step value — `ScaledResolution.ToGuiCoords` hit-test + drag tracking like the button, `onChange`
fires only when the snapped value changes). Controls:
- **VSync** (Off/On/Adaptive) and **Fullscreen** (On/Off) — buttons; setters push window-level state onto the
  live `ClientResources.Window` (`VSync`, `WindowState`).
- **Shadows** — a button cycling a **`ShadowQuality`** enum (Off/Low/Medium/High), which **replaced** the old
  on/off bool. It drives `WorldRenderer.ShadowDistance` (96/160/256) **and** `ShadowMapSize` (512/1024/2048);
  `WorldRenderer.EnsureShadowMap()` recreates `ClientResources.ShadowFramebuffer` (GL, in the shadow pass, main
  thread) when the size changes, and `uShadowMapTexel` (1/mapSize) is uploaded so the PCF disc tracks the new
  resolution. `GraphicsSettings.ShadowsEnabled` (≠ Off) gates the passes + `uShadowsEnabled`.
- **Render Distance** (slider, 4–24 chunks) — the flagship knob. The five coupled radii are derived from this
  one setting so the `load ≥ send ≥ render`, `cache > send` chain can't be violated: client draw =
  `WorldRenderer.RenderDistance` (a computed property reading the setting live → effect next frame);
  **singleplayer** `StateWorld.ApplyRenderDistance` also drives `ServerNetwork.ViewDistance` (= chunks·16),
  `WorldServer.TerrainRadius` (= chunks+1, volatile, read live by the LoadThread), and `WorldClient.CacheDistance`
  (= chunks·16 + `CacheHysteresis` 80; its setter resets the evict gate so a *decrease* evicts immediately).
  **Multiplayer** drives only the client draw distance (the client can't exceed what the remote server streams,
  and its cache stays at the safe default since it doesn't know the remote view distance — a proper
  `LoginAccept` view-distance advertise+clamp is deferred). `StateWorld.Update` re-applies when the setting changes.
- **FOV** (slider 30–110°, read by `StateWorld`'s projection), **Sensitivity** (slider, the `PlayerController`
  mouse-delta multiplier), **Brightness** (slider 0–0.3 → `uMinLight` in `Composition.fs`, the unlit floor).

`Program.Main` calls `GraphicsSettings.Load()` before creating the window and seeds `NativeWindowSettings` from
it, so the window opens with the saved vsync/fullscreen choice; the rest are read live each frame, so a change
takes effect immediately with no reload. Numeric setters clamp to the `Min*/Max*` consts. **No back-compat:** an
old `GraphicsSettings.json` deserializes with the dropped `Shadows` bool ignored and missing fields defaulted.
The dedicated server never touches `GraphicsSettings` — it keeps the default `ViewDistance`/`TerrainRadius`.

---

## Resource & plugin loading

`GuiResourceLoading` (client) and `MinecraftClone3Server/Program.LoadPlugins` (server) mirror each other:
`CommonResources.Load()` → add the `System` plugin → add every dir/zip in `Plugins/` →
**`PluginManager.AddResourcePacks()`** (cascade user packs, see below) → `PluginManager.LoadResources` →
`LoadPlugins`. **The server stops there; the client additionally does the GL-only steps**
(`ClientResources.Load`, `BoundingBoxRenderer.Load`, `EntityRenderer.Load`, `BlockTextureManager.Upload`).
Plugin model JSON and PNGs are read CPU-side via StbImage, so they load fine headless; only the texture-array
*upload* is GL.

**Animated textures (frame strips).** A texture whose height is a whole multiple of its width is a Minecraft
animation sheet (e.g. `water_still` = 16×512, 32 frames; also lava/fire). `ResourceReader.ReadBlockTexture`
detects this, and `BlockTextureManager.LoadAnimatedTexture` slices it into square per-frame layers and uploads
**all** of them, plus the `.mcmeta` `frametime`, into `BlockTextureManager.AnimatedTextures`. Block faces
currently sample **frame 0** only (textures are static), but every frame is retained so a future animator can
cycle them with no re-slice. Without this, a strip would land in the square texture array mismapped.

**Water.** Vanilla ships no cube model for water (its `model.json` is empty — vanilla uses a bespoke fluid
renderer), and `water_still.png` is a grey **tint-mask** (vanilla multiplies it by a per-biome water colour).
So `VanillaPlugin` authors a minimal cube model (`Vanilla/Models/Water.json`, parent `System/Models/Block`)
that references the **real** `minecraft:block/water_still` texture with `tintindex 0`, and `BlockWater`
returns the vanilla default water blue from `GetTintColor` (the mesher drops tint *alpha*, so the
translucency comes from the texture's own alpha, ~0.7). The block is decoupled from its *look*: the animated
surface + Fresnel sky reflection + sun specular (**Tier B**) live entirely in the deferred composition shader,
flagged by `BlockWater.GetRenderMaterial` (see "Water surface — Tier B" in the rendering section). A refractive
forward water pass (Tier C) is still deferred (see "Known rough edges").

Because server-side light simulation calls `Block.GetLightLevel`, **block code that runs on the server must
not touch client/GL/window state** (this is what crashed `BlockTorch` — it read the keyboard).

Content staging (see the two exe `.csproj` files): the `System` plugin (shared, from
`MinecraftClone3/Content/System`) and `VanillaPlugin` (its content + freshly built DLL under `Dlls/`) are
copied next to each executable so both resolve `Plugins/` against `AppContext.BaseDirectory`.

**The resource layer is a generic path→bytes cascade** (`ResourceManager`). `AddFileSystem` indexes every
file under a source's `Assets/` prefix (case-insensitive) keyed by the path *after* the prefix, storing the
`(FileSystem, original full path)` so reads go back through the source's *own* casing — a real Minecraft jar
holds lowercase `assets/...` and `ZipArchive.GetEntry` is case-sensitive on every OS, so `LoadAsset` must
read by the stored path, not by reconstructing `"Assets/" + key`. Nothing is parsed at index time except
`Lang/*.lang` (eagerly merged); models/textures are parsed **on demand** by the consumers
(`ResourceReader.ReadBlockModel`/`ReadBlockTexture`, cached), so only the handful of referenced assets of a
jar's thousands ever get decoded. **Load order = cascade priority:** System → plugins → resource packs, and
within packs by name order; a later-loaded source containing a key wins (`ResourceSettings` order).

**Resource packs** live in `GamePaths.ResourcePacksDir` (`~/.local/share/MinecraftClone3/ResourcePacks/`,
created on access with a `README.txt`). `PluginManager.AddResourcePacks()` scans it **after** the plugins —
subdirectories → `FileSystemRaw`, `*.zip`/`*.jar` → `FileSystemCompressed`, sorted by name, each in a
try/catch — and `AddResourcePack` indexes each (assets + lang only; **no `PluginInfo.json`/DLL**, so a plain
client jar loads without the "no info file" error). An empty dir logs a `Logger.Warn`.

**The Vanilla plugin ships no models or textures** — those are Mojang-derived; it ships only code + its
`Lang/`. It references blocks by **explicit Minecraft resource locations** (`minecraft:block/stone`,
`minecraft:block/grass_block`, `minecraft:block/torch`, …) and the engine loads the real Minecraft model
JSON + PNGs from a user-provided pack (a 1.13+ client jar, or any pack) dropped in `ResourcePacks/`. The
vanilla model format **is** the engine's format (`parent`/`textures` `#vars`/`elements`/`faces`); extra
fields (`shade`, `gui_light`, element `rotation`) are silently dropped by `JsonConvert.PopulateObject`. The
only gap is **reference syntax**: Minecraft uses namespaced resource locations `[namespace:]path` (default
`minecraft:`; the `models`/`textures` category is implied by context). `BlockModel.GetRelativePaths` resolves
these by **appending** a candidate `{ns}/{category}/{loc}{extension}` (split `ns:`, default `minecraft`;
`.json`→`models`, `.png`→`textures`) to its existing relative candidates, and **both** `ReadBlockModel` and
`ReadBlockTexture` run the same resolution on the location they're given — so `minecraft:block/stone` finds
`minecraft/models/block/stone.json` and `minecraft:block/black_stained_glass` finds
`minecraft/textures/block/black_stained_glass.png` from the pack. The candidate is purely additive, so the
System plugin's relative refs (`parent: CubeAll`, `Textures/Blocks/MissingTexture.png`) still resolve and the
no-pack fallback is unchanged. The plugin pins to **1.13+ vanilla naming** (singular `block/`,
`grass_block_*`, `*_stained_glass`) — a different jar version may need ref updates. `minecraft-assets/`
(gitignored, extracted from a client jar) doubles as a ready `FileSystemRaw` dev pack.

**No pack present:** models/textures fail to resolve → existing `MissingModel`/`MissingTexture` fallback;
the game runs with placeholder blocks, no crash (the headless server tolerates an absent pack — model parse
is GL-free and only feeds the client mesher). **The GUI font is also pack-sourced** — `Font` (`Client/GUI/
Font.cs`) loads `minecraft/font/default.json` and its `minecraft:font/*.png` bitmaps straight from the pack
(the System plugin ships no font any more), so with no pack `Font.Load` logs an error and text rendering is
disabled (the rest of the game still runs). **The sky's sun/moon textures are likewise pack-sourced** —
`WorldRenderer.LoadSkyTextures()` (called right after `Font.Load`) reads `minecraft/textures/environment/
{sun,moon_phases}.png` from the pack; with no pack the skybox draws procedural discs instead (see "Background
sky").

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
  drawn / total** (frustum-cull readout), shadows on/off, loaded/mesh/upload queue depths, a **pipeline-backlog
  line** (`apply`/`ready`/`dispose` client queue depths, plus `stage` = the server staging queue in SP) that
  surfaces the upstream backlogs the mesh/upload depths don't, and player pos + chunk.
  `RenderDebug.ShowDiagnostics`, drawn in `StateWorld.DrawDiagnostics` from `RenderDebug` fields
  (`DrawnChunks`/`ShadowPass` written by `WorldRenderer`; `FrameMs`/`GpuMs`/`UpdateMs` by
  `GameClient`) plus the `WorldClient`/`WorldServer` lock-free depth mirrors. This is the cheap,
  always-available live HUD; the CSV profilers (F10) are the heavy tools. (Per-frame *sum* timers are
  deliberately kept off F3 — a single-frame sum flickers meaninglessly at 120 Hz; they live in the CSV.)
- **F4** — toggle chunk-border wireframes around the player (current chunk red, neighbours yellow).
  Code: `Client/Graphics/ChunkBorderRenderer.cs`, drawn in the geometry pass (depth-tested). (F5/F6 are
  unused — F5 was the occlusion-cull A/B toggle, removed with the cave cull; F6 was the cascade tint.)
- **F7** — toggle the raw shadow-factor view: composition outputs the per-pixel shadow term as greyscale
  (white = lit, black = shadowed), isolating the shadow test from lighting to spot acne/peter-panning.
  `RenderDebug.ShadowFactor` → `uDebugShadow` (`Composition.fs`). (F6 is unused — it was the cascade-tint
  debug, removed with CSM.)
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
  total index count uploaded this frame), upQ (upload-queue depth),
  diskMs/genMs/applyMs/meshMs/drainAddMs (per-frame wall-clock of the heavy pipeline stages — server
  disk-load vs world-gen [split out of the old lumped loadMB], client chunk decode, client CPU mesh, and
  the main-thread server stage-drain; the background ones are Interlocked tick sums attributed to the
  render-frame interval, so they overlap real time, not add to updateMs),
  chFromDisk/chGenerated/chApplied/chMeshed/chDrainedAdd (throughput: chunks crossing each of those stages
  this frame — chGenerated≫chFromDisk means a fresh world is regenerating, not loading from save),
  srvStageQ/applyQ/renderReadyQ/disposeQ (the previously-hidden pipeline queue depths: server
  _chunksReadyToAdd, client _applyQueue [decode backlog], _renderReady [decoded→GL backlog], _disposeQueue —
  together with the existing pendingMesh/upQ these cover the whole chain, so a balloon localizes *which*
  stage is the wall)`. `frameMs` is the
  real frame interval (catches drops); `updateMs`/`renderMs` are CPU work. The four stalls a CPU sampler
  **can't** see, isolated so a high `frameMs` with tiny `updateMs`/`renderMs` is attributable: `swapMs`
  = the `SwapBuffers` call; `gapMs` = OpenTK's between-frame `NewInputFrame`+`ProcessWindowEvents` (the
  GLFW poll, where an **async/vsync present surfaces on Linux/GLX** even though `SwapBuffers` itself
  returned instantly); `gpuMs` = actual GPU render time (`GL_TIME_ELAPSED` query). `gpuMs` large ⇒ GPU-bound;
  `gpuMs` small but `gapMs` large ⇒ present/event overhead, not the GPU. `shadowMs`/`geomMs`/`compMs` split
  `gpuMs` into the three deferred passes — the shadow depth pass, the G-buffer geometry pass, and
  the fullscreen composition (PCF) — via `GL_TIMESTAMP` marker queries (`Client/Graphics/GpuTimers.cs`,
  populated only while recording, so a normal run issues no extra GL; the markers coexist with the whole-frame
  `GL_TIME_ELAPSED` because timestamps don't nest). **Both the whole-frame and the per-pass timers read back
  from a query ring harvested newest-ready, NOT a 1-frame ping-pong:** with vsync **off** the CPU runs several
  frames ahead of the GPU, so last frame's query usually isn't finished when read — a 1-frame read would
  perpetually see "not available" and freeze `gpuMs`/`shadowMs`/`geomMs`/`compMs` at a stale constant (this
  actually happened: a vsync-off capture pinned `gpuMs` at a single value and all per-pass at 0). The ring
  gives the GPU many frames to finish; reads still never stall (only consume already-available results). They
  localize a GPU-bound frame: large `shadowMs` ⇒ the depth pass is geometry/draw-call-bound
  (the shadow map redraws all in-range opaque chunks from the sun's POV; see the shadows note), large `compMs` ⇒ the
  composition is fill/shader-bound (the 12-tap PCF, which the per-draw/triangle view of a RenderDoc capture
  can't see). Their sum is **< `gpuMs`** (the remainder is GUI + clears + present setup); a skipped shadow
  pass logs `shadowMs = 0`. ⚠️ With **vsync on**, the composition pass (the first to write the default
  framebuffer) absorbs swapchain back-pressure, so `compMs` reads a vsync-quantized stall, not real fill —
  read `compMs` only from a **vsync-off** capture. When `updateMs` is large instead, srvMs/netMs/cliMs and their sub-splits localize it — e.g.
  `upMs ≈ updateMs` ⇒ the GL upload (re-`BufferData` of edited chunks) is stalling, not lighting/meshing
  (which are off-thread). `updCalls` is `OnUpdateFrame` calls per render frame (OpenTK fixed-timestep
  catch-up); ≫ 1 means updates are running behind and being batched.
  The profiler reads only lock-free mirrors (`WorldClient.LoadedChunkCount`/`MeshQueueDepth`/`UploadQueueDepth`/
  `ApplyQueueDepth`/`RenderReadyQueueDepth`/`DisposeQueueDepth`, `WorldServer.StageQueueDepth` — `volatile int`
  or `Interlocked`-maintained on the writing threads) — **never** `ConcurrentDictionary`/`ConcurrentQueue.Count`
  (all-stripe / segment-snapshot) or a `_meshLock` take — so recording with F10 on doesn't contend with the
  apply/mesh threads and inflate the very stutter it measures. The per-stage timers are
  `Stopwatch.GetTimestamp()` tick deltas measured unconditionally (a cheap pair, like the existing alloc
  brackets) and only *read+zeroed* in `Record`.
  Code: `Util/Profiler.cs`, fed from `GameClient.OnRenderFrame` + `StateWorld.Update` (phase times) and the
  `WorldServer`/`WorldClient` pipeline threads.

  **F10 drives two CSVs, split by *grain* not subsystem** (a per-frame time-series and a per-chunk latency
  log — they answer different questions, so frame-bucketing the per-chunk one would lose it). `profiling.csv`
  above is one row per render frame (rates & queue levels, sampled at frame cadence). `chunk-trace.csv`
  (`Util/ChunkTracer.cs`, same F10 toggle via `Profiler.Start`/`Stop`, same `GamePaths.UserDataDir`, same
  `t` clock so they correlate offline) is **one row per chunk**, emitted when the chunk finishes uploading.
  Its schema is a **work-vs-wait decomposition** of the chunk's whole life across the 4 pipeline threads,
  keyed by chunk position (the only identity stable across the `CachedChunk→Chunk→ChunkRenderData` handoffs;
  a side `ConcurrentDictionary` threads the timestamps through). Columns: `t, posX/Y/Z, source`
  (disk/gen/edit/stream), `mp`, then the tiling spans `genMs`(work) `stageWaitMs` `drainWaitMs` `streamWaitMs`
  `netWaitMs` `applyWaitMs` `applyMs`(work) `meshWaitMs` `meshMs`(work) `uploadWaitMs`, and `totalMs`.
  Adjacent stamps tile the timeline, so in **singleplayer** the spans sum to `totalMs` — an instrumentation
  self-check. **Caveats:** a **multiplayer client** has no in-process server, so the server stages
  (`genMs`..`netWaitMs`) are blank and `totalMs` starts at `applyWaitMs` (`mp=1`, `source=stream`); a
  **block-edit** (or a re-stream after eviction) emits a separate `source=edit` row covering only the
  mesh→upload tail; chunks mid-flight when recording starts are dropped (clean boundary). Memory is bounded
  by a `MaxLive` cap (drop-new, counted + logged), explicit `Abandon` at the drop sites (empty chunk, server
  evict, client `UnloadChunk`), and a TTL sweep. Every stamp early-outs on `!Profiler.Recording` before
  taking a timestamp, so a non-recording run pays nothing.

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
is navigable: the passes nest as **Shadow / Geometry → Opaque/Transparent/Overlays / ShadowResolve /
Composition** (RenderDoc shows per-group GPU time — the per-pass breakdown for free, no in-engine timing),
and the G-buffer/shadow targets and shader programs get names. Every call is a no-op unless `Enabled`
(same `RENDERDOC_CAPOPTS`/`MC3_FORCE_X11` detection, or `MC3_GL_DEBUG=1`), so normal runs and macOS (no
`KHR_debug`) never touch the entry points. **A depth-only pass being the GPU bottleneck means
geometry/draw-call-bound, not fill/shader-bound** — the shadow pass redraws all in-range opaque chunks from
the sun's POV, so the fix is reducing geometry submitted to it (a shorter `ShadowDistance`, a smaller
`ShadowMapSize`), not shader work.

**Automated flythrough benchmark (`--benchmark`).** A GTA-style profiling session: the client boots straight
into a fresh fixed-seed world and an automated camera flies a deterministic scripted path while the full
`Profiler` + `ChunkTracer` record, then prints a percentile report and exits. It is the reliable, repeatable
way to measure a render/pipeline change — *not* hand-flying with F10. Code: `Util/Benchmark.cs` (engine, so it
ties together `Profiler`/`WorldRenderer`/the camera), wired from `Program.Main` (CLI parse + vsync-off +
settings pin), `GuiResourceLoading` (launches a benchmark `StateWorld` instead of the menu), `StateWorld`
(benchmark ctor: drives the camera via `Benchmark.DriveCamera` instead of player input, pumps the server
unfocused), and `GameClient.OnRenderFrame` (`Benchmark.Tick` per frame, `Close()` on `Finished`).

```
dotnet build MinecraftClone3.sln -c Release            # ALWAYS benchmark Release — Debug understates FPS hugely
bin/Release/net10.0/MinecraftClone3 --benchmark        # boots straight into the flythrough, prints report, exits
  --benchmark-seconds=60   # recorded duration (default 60)        --benchmark-warmup=6   # un-recorded settle
  --benchmark-seed=1337    # world seed                            --benchmark-rd=8       # render distance (chunks)
  --benchmark-shadows=Medium  # Off|Low|Medium|High                --benchmark-edits=off  # skip the edit phase
  --benchmark-time=220     # pinned day-clock seconds (sun pos)
```

The run is split into four phases that each stress a different part of the pipeline, reported separately:
**streaming** (cruise into virgin terrain → gen + mesh fill), **orbit** (360° pan over fresh ground → frustum
/ shadow-map churn + heavy streaming), **return** (fly back over evicted ground → re-stream / regen +
despawn), and **edit** (skim low, break/place each frame → light BFS + delta + remesh). Determinism: the world
is wiped+regenerated each run (`Worlds/__benchmark__`), the camera path is a pure function of elapsed time
(frame-rate independent), and the **day clock is pinned** (`WorldRenderer.FixedTimeOfDay`, default mid-morning
sun-up so the shadow passes run — the heavy case). Settings are **pinned in-memory** (render distance / shadow
quality / FOV / brightness) and **`GraphicsSettings.SuppressSave` keeps the user's `GraphicsSettings.json`
untouched** (verified byte-identical after a run). The report (also written to `benchmark-report.txt`) gives
overall + per-phase avg / 1%-low / 0.1%-low FPS, **an UNCAPPED FPS = 1000/max(gpu,cpu) work time** (the
present/pacing-independent engine throughput; observed FPS additionally folds in frame-time hitches), frame-ms
percentiles, the GPU per-pass split (shadow/geom/comp), CPU update/render ms, drawn chunks, and GC/alloc.

> ⚠️ **Benchmark only on an idle machine.** The UHD 630 is an integrated GPU sharing the CPU package's
> thermal/power budget, so concurrent CPU load (a parallel build, another process, an agent workflow doing
> file I/O) **down-clocks the iGPU** — a captured run showed every GPU pass inflate ~70%, including the
> fixed-cost fullscreen composition pass (a tell-tale: a constant-work pass getting slower ⇒ the clock
> dropped, not the work). Run baseline and optimized builds back-to-back on a quiet machine to control for
> thermal state; a single confounded run is worthless for an A/B.

### Performance findings (what the benchmark established)

Load-bearing facts for anyone making the renderer faster — read before optimizing, they save dead ends. Two
hardware profiles were measured: the **integrated UHD 630** (the dev laptop's default GPU) and the **dedicated
NVIDIA GTX 750 Ti** (PRIME-offloaded — see "Running on the dedicated GPU" below).

- **The FPS cap was `GameWindowSettings.UpdateFrequency = 120`, NOT the display.** OpenTK 4.9 runs the whole
  loop (update+render) at `UpdateFrequency`; 120 → a hard 8.33 ms/frame software cap that pinned observed FPS
  at ~118 regardless of GPU/CPU (this is what made the game "not reliably profilable" — the headline FPS
  measured the cap, not the engine). It is now **`UpdateFrequency = 0` (uncapped)** in all modes; frame pacing
  is VSync's job (SwapBuffers blocks at the refresh when on). The benchmark also reports an **UNCAPPED FPS =
  1000 / max(gpu work, cpu work)** — present/pacing-independent, the metric to optimize against and compare
  across machines (observed FPS additionally folds in frame-time hitches).
- **The geometry pass is triangle / primitive-setup bound, NOT fill, bandwidth, draw-call, or overdraw
  bound.** Every other suspect was ruled out by measurement: (1) batching all opaque draws into one
  `glMultiDrawElementsBaseVertex` cut `renderMs` ~3.7→2.0 but left GPU time unchanged (Mesa non-indirect
  multidraw is a CPU loop); (2) removing the cutout `discard` to enable early-Z changed `geomMs` ~5 % (overdraw
  is negligible — exposed-face meshing); (3) packing the vertex 72→32 B (2.25× less bandwidth) barely moved
  `geomMs` (not bandwidth-bound); (4) the fullscreen composition pass is ~0.18 ms (fill is cheap). `geomMs`
  scales linearly with **drawn-chunk count** (≈ triangle count), so the lever is **fewer triangles**.
- **500 FPS at render distance 16 / shadows off IS achieved on the GTX 750 Ti** (the goal config), via three
  stacked, mostly-lossless wins on the triangle/CPU bottleneck: **(a) packed 32-byte vertex** (halves mesh
  alloc + upload, fixes the streaming GC hitches); **(b) distance LOD** (coarse stride-2/4 meshing of distant
  chunks — `geomMs` 4.8→1.2 ms, the big one); **(c) distance-first visible-set cull** (skip the ~3000
  out-of-range loaded chunks before the frustum test). Result: **OVERALL ~500–556 observed / ~620–670 uncapped
  FPS**, every phase >500 uncapped, frame work ~1.5 ms (gpu ~1.4 / cpu ~0.9). From the 102 FPS capped start,
  ~5×. Only the LOD trades quality (distant coarsening, near is full detail) — the user opted into it; the
  rest is lossless.
- **At FULL quality on the UHD 630 (Medium shadows, RD 8, 720p) the frame is GPU-bound at ~9 ms ≈ 100–108 FPS
  uncapped** (per-pass: shadow ~3.4, geom ~4.5, comp ~1.1; CPU not the wall). 500 FPS there is **not** reachable
  without quality cuts — it needs ~4–5× and the passes are real raster work. The `--benchmark-shadows` /
  `--benchmark-rd` sweep quantifies the tradeoff curve (UHD 630 uncapped: Medium/RD8 ≈ 100, shadows-off ≈ 145,
  shadows-off+RD4 ≈ 400–650). The same LOD + packing wins help the UHD 630 too, but its fill/shadow floor keeps
  full-quality 500 out of reach on that GPU.
- **Remaining hitches at RD 16 are Gen2 GC pauses** (~1 % of frames, ~15–30 ms) from the large resident heap
  (~4400 chunks) + streaming/LOD-remesh allocation churn — they drag the *observed* average a touch below the
  *uncapped* ~620. Lossless smoothing still on the table: cut the LOD-remesh allocation, a `ChunkRenderData`
  pool, or GC latency tuning.

### Running on the dedicated GPU (NVIDIA PRIME offload)

The dev machine is a hybrid laptop (Intel UHD 630 default + NVIDIA GTX 750 Ti). Launch on the NVIDIA GPU with
PRIME offload + the X11 backend (`MC3_FORCE_X11`, needed because RenderDoc/GLX paths and the offload prefer
X11):

```
__NV_PRIME_RENDER_OFFLOAD=1 __GLX_VENDOR_LIBRARY_NAME=nvidia MC3_FORCE_X11=1 __GL_SYNC_TO_VBLANK=0 \
  bin/Release/net10.0/MinecraftClone3 --benchmark --benchmark-rd=16 --benchmark-shadows=Off
```

(`__GL_SYNC_TO_VBLANK=0` and the benchmark's forced VSync-off keep it uncapped.) **Benchmark Release, not
Debug** — Debug understates FPS hugely. The 500 FPS figure is Release.

## Conventions

- **Comments:** self-documenting code. Only `///` XML doc comments where they earn their place — **no
  inline `//` narration** of what the next line does.
- Match the surrounding code's style, naming, and comment density.
- **No backwards compatibility.** The project is in rapid development with no shipped users. Do **not**
  add format-version negotiation, save migrations, deprecation shims, or compatibility fallbacks. When
  the on-disk or wire format changes, the world is simply regenerated (delete the world folder under
  `Worlds/`, or `World/` for the server). Prefer the
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
  The upload loop is **time-budgeted** (`UploadBudgetMs`, ~4 ms/frame), not capped at a fixed chunk count: the
  mesh **pool** produces chunks faster than one frame can upload, so a fixed cap (was 8/frame) throttled the
  world-fill below the pool's output (an F10 `chunk-trace` showed the upload queue ballooning to ~900 and
  `uploadWaitMs` becoming ~64% of a chunk's load latency). Uploads are cheap (~0.15 ms each), so the budget
  drains tens per frame while bounding the upload's frame cost. Failures (mesh thread mid-remesh) are collected
  and requeued *after* the loop, so each queued position is dequeued at most once per frame (no same-frame spin).
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
  scan) — and `BuildVisibleSet` + the shadow caster cull iterate it by index
  (contiguous, cache-friendly, zero allocation, never an enumeration). `RenderData` stays a
  `ConcurrentDictionary` purely for by-position lookups
  (`DrainRenderReady`, the upload loop, the mesh thread's `TryGetValue`, `UnloadChunk`). The
  profiler's `renderData` column now reads
  `RenderList.Count` (a field read) instead of `RenderData.Count` (which acquires **all** of the
  dictionary's locks) — that `.Count` ran every frame in `Profiler.Record`, *after* the `renderMs`
  stopwatch stops, so it landed in `frameMs` but not `renderMs`; with F10 on (the maintainer profiles with
  it on) that was unmeasured main-thread overhead.

## Known rough edges / deferred work

- **Player physics is the "80%" walk model — several exact-MC behaviours are deferred.** Implemented: gravity,
  jump, swept per-axis AABB collision, Ctrl sprint, walk/fly toggle, **auto-step up `StepHeight` (0.6 = MC)
  ledges** (climbs slabs/partial blocks, still jump for a full cube). **Not** implemented: sprint-jump forward
  boost, sneaking (no crouch/edge-stop), per-block slipperiness (no ice/slime blocks exist), and
  **collision for creative flight** (flight is deliberately noclip, to keep the original free-fly).
  Non-cube collision now exists (multi-box `GetCollisionBoxes`, used by stairs). The exact-constant *ordering*
  (gravity-before-move vs after) may be a tick off MC and is tunable in `PlayerPhysics.Tick`.
- **Stairs are the "straight, 80%" stair.** `VanillaPlugin/Blocks/BlockStairs.cs` (`Vanilla:OakStairs`) uses
  the real `minecraft:block/oak_stairs` model from the pack; orientation (facing bits 0-1, top-half bit 2) is
  applied as a mesh-time `GetModelTransform` rotation + matching multi-box L collision, since the engine reads
  no blockstate. Deferred / accepted: **no corner (inner/outer) variants**; the **raytrace/outline + targeting
  is the full cube** (not the L), so the highlight covers a bit of air over the low step; rotated faces keep
  their un-rotated normals/`cullface` (mildly off flat shading, harmless culling); facing rides whole-chunk
  resends like tinted glass. The **yaw→facing mapping** (high step toward the player's look) and the top-half
  X-flip are the visual-tuning knobs — flip the sign / axis in `BlockStairs` if placement reads reversed.
- **Walking into a not-yet-streamed chunk reads as air (could fall through an edge).** Collision uses
  `WorldBase.GetBlock`, which returns air for unloaded chunks, so a solid block in an un-streamed chunk
  doesn't collide. Bounded in practice: the join handshake pre-streams the spawn column, and the client cache
  distance (240) stays well ahead of the server view distance (160), so terrain is normally resident before
  you reach it. A fast clip into ungenerated space could still drop the player; accepted for now.
- **Water is Tier B (animated normals + Fresnel sky reflection + sun specular); Tier C is deferred.** Tier B
  is **done** — see "Water surface — Tier B" in the rendering section for the full design (in-shader, no extra
  pass; flagged via `normal.w`; per-attachment blend so the flag survives; reflects the procedural skybox —
  **not** a cubemap). Residual edges accepted for now: the reflection is the shared **`SkyColor`** skybox
  (gradient + sun/moon/stars; see "Background sky"), so it tracks time of day but still **doesn't reflect
  terrain or clouds** (no real environment capture); there is **no refraction or depth-based absorption**
  (looking down still shows the Tier-A tint over the bottom, just Fresnel-mixed) — that's **Tier C** (a
  **forward water pass after composition** reading the opaque scene colour/depth), still the deferred next
  step. Water is also **not a fluid**: it doesn't flow, level, or fill — it's a static block placed by gen
  below sea level. Look knobs are shader `const`s (wave amp/freq/speed, `WaterF0`, `WaterSpecExp/Gain`).
- **Animated textures show frame 0 only.** Strip textures are sliced and *all* frames are uploaded +
  retained (`BlockTextureManager.AnimatedTextures` with `frametime`), but nothing cycles them yet. Adding the
  animator (advance the sampled layer over time, by remesh-free means — a per-animated-texture uniform/layer
  swap) is the deferred future path. The `.mcmeta` `frames`/`width`/`height` reorder fields are ignored (only
  square top-to-bottom strips at default order are handled — covers water/lava/fire).
- **Biome height blends; surface *blocks* and climate selection don't.** Terrain height is bilinearly
  blended across biome borders (see pipeline step 1 — `HeightBlendSpacing` lattice), so Mountains↔Plains is a
  foothill, not a cliff. Three accepted residual edges: (1) **surface blocks still snap** at the border
  (grass↔sand) — only height blends, intended (matches MC); (2) **thin-biome skipping** — a biome strip
  narrower than `HeightBlendSpacing` may touch no lattice corner and contribute nothing to the height blend
  (rare here — climate biomes span ≫ the spacing; mitigated by a smaller spacing); (3) **mountain-on-coast
  sand shelf (cosmetic)** — if a high-bias land corner within the spacing lifts an ocean-classified column's
  blended height above `SeaLevel`, that column's hard Ocean skin (sand) shows a small shelf above water
  (`ocean`/water follow the blended `surf`, so no flooding/voids — just the shelf). Tune `HeightBlendSpacing`
  (16 steeper / 32 gentler). Ocean/Beach are **height-derived overrides** on the base height, so an
  ocean-*variant* biome from a plugin isn't selectable (the climate source only sees land-tagged climate
  biomes); supporting plugin ocean biomes needs a small extension. Deferred.
- **Gen skips the light BFS, so some sky/light is approximate** (self-corrects on the first nearby edit, which
  triggers the real BFS): no lateral sky spill into cave mouths or under overhangs, dense canopy doesn't shadow
  the ground (leaves keep their seeded sky 15), deep water dims by a simple per-block gradient not a flood.
  Caves carved below sea level are **air, not water** (no fluid fill).
- **Decoration determinism relies on the ±1-chunk margin + bounded feature reach.** A feature that writes more
  than ~1 chunk from its origin column would clip at borders (keep tree/vein extents small). Decoration is also
  recomputed per *vertical* chunk in the band (the RNG is Y-independent so it's consistent, but a tall column of
  air chunks re-runs the feature attempts before clipping them away) — bounded by the local surface-height gate;
  a tighter per-step Y gate is a possible optimization. Ore/`SurfaceHeight` recompute inside the carver/features
  duplicates the per-column noise the fill already did — background cost, not yet memoized.
- **Single active dimension.** `WorldServer` binds one dimension (`Vanilla:Overworld`); multi-dimension travel
  and per-chunk dimension metadata in saves are deferred. The generator's column scratch assumes the single
  LoadThread writer (Invariant 5).
- **Resident-chunk growth from the vertical band.** `TerrainRadius` (10) × the full `MinChunkY..MaxChunkY`
  (9 chunks) is a much larger loaded set than the old thin surface slab; the `UnloadThread` still evicts idle
  chunks but the working set is bigger. Tune `TerrainRadius`/`ChunkLifetime` if memory matters.
- **Sun shadows are one low-res map capped at `ShadowDistance` (160).** A single shadow map covers
  `[ShadowNear, ShadowDistance]` and fades out at the edge; this now equals `RenderDistance` (160), so all
  drawn geometry is within the shadowed range. The map is intentionally low-res (the **default favours low-res/soft
  shadows** — `ShadowMapSize` 1024 over the whole distance — as an art-direction choice, not a cap). Raising
  `ShadowMapSize` (sharper) or `ShadowDistance` (more coverage, coarser texels) trades one for the other; a
  much larger distance would eventually want a distorted (warped) map or cascades to keep near detail, but CSM
  was deliberately removed for simplicity (see the shadows section) and isn't coming back. Bias is a
  scene/driver-dependent tradeoff (normal-offset + polygon-offset + depth bias): too little ⇒ acne, too much ⇒
  peter-panning — the constants (`NormalBias`/`DepthBias` in `ShadowResolve.fs`,
  `ShadowStrength`/`ShadowSoftness`/`ShadowCasterExtent`/`GL.PolygonOffset` in `WorldRenderer`) may need a pass
  on the target GPU (Mesa). **`ShadowMapSize` (C#) and the `ShadowTexel` constant (`ShadowResolve.fs`) must be
  changed together.**
- **The shadow depth pass is a fixed per-frame cost (resolution-independent) — except it's now skipped in
  caves.** It redraws all in-range opaque geometry from the sun's POV every frame regardless of window size, so
  it hits hardest at *low* framerate headroom, not specifically at fullscreen (the fullscreen drop is the
  resolution-scaled G-buffer + composition fill; the composition early-outs above target that). It is **gated
  on `_anyShadowReceiver`** (a visible sky-exposed chunk within `ShadowDistance`, set by the visible-set scan
  — see the rendering section), so deep in a cave it's skipped entirely; above ground it still runs every frame
  and can't be skipped/cached there because the sun moves every frame (the light matrix changes). The
  immediate knobs are a shorter `ShadowDistance` (less geometry per pass) or a smaller `ShadowMapSize`; deeper
  optimization (e.g. re-rendering the map every N frames with a slightly stale sun) is deferred.
- **No texel snapping → mild shadow shimmer while the camera moves.** Because the sun moves every frame,
  the shadow projection is intentionally unsnapped (snapping would flicker — see the shadows section), so a
  fixed-grid stabilization isn't available. The cost is faint sub-texel edge crawl while *walking/turning*;
  PCF softens it and the sun's own crawl masks it. If the day cycle were ever paused or made very slow,
  re-introducing texel snapping (snap the map centre to its light-space texel grid) would be worth it.
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
- **Meshing throughput is the chunk-fill cost — now parallelized, two amplifiers remain.** An F10 end-to-end
  trace (`chunk-trace.csv`) pinned the slow world-fill on the mesh stage: chunks generated in ~2 ms then waited
  **~7.5 s (99.6 % of their latency)** in the mesh queue, the single mesh thread pegged at 100 % the whole
  recording, queue depth ~1000+. The mesh stage is now a **worker pool** (`Environment.ProcessorCount-2`, ≥1)
  draining the shared queue — it parallelizes safely (one chunk per worker via the `_meshPending` claim, read-only
  chunk access, GL on the main thread). Two amplifiers are still **deferred**: (1) a single edit (or each
  newly-applied chunk during streaming) **full-remeshes the chunk plus up to six face neighbours** — the trace
  showed ~2× mesh ops per settled chunk — fixable by remeshing only the affected sub-region; (2) each remesh is
  tens of ms because of per-vertex smooth-lighting (~4 `GetBlockLightLevel` + `IsFullBlock` *per vertex* over the
  whole min..max box), fixable by caching per-vertex brightness. (The earlier "~10 FPS / ~100 ms `updateMs` spikes when
  destroying" was **misdiagnosed** as swap/GPU-bound — it was the main thread blocking in `ChunkRenderData.Upload`
  on the mesh thread's VAO lock during these remeshes. The F3 CSV that looked like a 10 FPS *baseline* was
  captured during *continuous* destroying; standing idle renders fine, confirming the pipeline/GPU was never the
  bottleneck.)
- **Chunk saves are per-chunk, not batched per region.** `UnloadThread`/`Unload()` call `SaveChunk` once per
  dirty chunk, each doing one 256 KB index rewrite. Batching all of a region's dirty chunks into a single
  index rewrite is deferred — marginal now that the index is 64× smaller, and `SaveChunk` already early-outs
  on `!NeedsSaving`.
- **Sky light is "gen-seed + simple BFS" — known edit-time limitations (accepted scope).** At chunk-gen the
  `NoiseChunkGenerator` seeds the sky container directly (open air above the surface = 15, water dims with
  depth, caves stay 0; see the world-generation section), so untouched terrain is well lit without a flood.
  On edit, `UpdateSkyValues` floods sky like block light but with two simplifications: (1) **sky
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
- `StateWorld` connects synchronously on the main thread; a far/unreachable MP host briefly blocks.
- `ClientSession.SentChunks` shrinks only on `ChunkRelease`/dirty resend, so a misbehaving or crashed client
  could leave stale entries until it disconnects. Bounded in practice by client `CacheDistance` eviction;
  fine for a hobby project, would need a server-side cap/timeout for hardening.

# Networking

`Networking/Packet.cs` defines the `PacketId` enum, the `Packet` base (`Write`/`Read` over
`BinaryWriter`/`BinaryReader`), the id→constructor factory, and `Serialize`/`Deserialize`. Each packet is
its id byte followed by its payload. `TcpConnection` frames packets with a 4-byte little-endian length
prefix and serializes both ways. `LoopbackConnection` (singleplayer) instead **passes the `Packet` object
by reference** — no serialize/deserialize — because both endpoints are pumped sequentially on the client's
main thread and the server builds a fresh packet per `Send` it never reads back, so there's no shared
mutable state to race on. The wire packets are identical; only the in-process transport shortcuts them.

`ChunkData` follows this through: compression + serialization are **lazy, inside `ChunkDataPacket.Write`/
`Read`** (the TCP transport boundary), not in `From`. `From(chunk)` carries the live `Chunk` by reference.
Over loopback `Write`/`Read` never run, so the SP streaming path does **no GZip and no (de)serialize at
all** — the carried `Chunk` is cloned by `new Chunk(world, source)` (a paletted copy: small palette + packed
index arrays; uniform chunks copy almost nothing). Over TCP `Write` serializes+GZips; `Read` only copies the
still-compressed bytes into `CompressedData` (a cheap memcpy) and the client decompresses + deserializes
later. **Both transports decode on the client's background apply thread, not the render thread.** The clone
tolerates the server mutating the source chunk concurrently (a torn entry self-corrects via the next
`BlockChanges` delta), made safe by the palette copy-on-grow rule.

The TCP `Chunk.Write` payload and `ItemStack` (in `InventoryState`/`InventoryAction`/container packets) encode
blocks/items by stable registry **name**, not numeric id (the same self-describing form as disk — see
[world-model.md](world-model.md)); the client resolves names to its own session-local ids on decode. The
compact `BlockChanges`/`PlaceBlockRequest` paths still carry numeric `blockId` — fine because ids are assigned
in deterministic plugin order, so client and server agree within a session.

```
  Packets (Networking/Packets.cs)
  C→S  Login                 announce (carries the player name, used as the inventory save key)
  S→C  LoginAccept           assigns entity id + spawn (applied behind the loading screen)
  S→C  PlayerReady           spawn column streamed → client may apply spawn + enter the world
  S→C  ChunkData             Vector3i + Chunk (loopback: by ref; TCP: GZip of Chunk.Write)   (initial streaming only)
  S→C  BlockChanges          ChunkPos + (localIndex, blockId, light, sky)[]   (edits + block-light + sky-light)
  C→S  ChunkRelease          client dropped a chunk from its cache; clears its SentChunks entry
  C→S  PlaceBlockRequest     pos + block id (id 0 = break) + placement metadata (computed client-side)
  C→S/S→C  EntityMove         own player up; relayed to others down
  S→C  EntitySpawn/EntityDespawn   remote players appearing/leaving
  S→C  WorldTime             world clock in seconds (TickCount·SecondsPerTick); on join + ~1/s, drives day/night
  S→C  LodColumnData         Phase-2 distant horizon: one region of surface-only LOD columns (loopback: by ref;
                             TCP: GZip), streamed nearest-first BEYOND the chunk view distance (see rendering.md)
  S→C  InventoryState        full 36-slot inventory + selected hotbar slot; sent once on login (see inventory.md)
  C→S  InventoryAction       slot index + ItemStack; client edited a slot in the creative screen
  C→S  HeldSlot              selected hotbar index changed (number key / scroll wheel)
  C→S  DropItemRequest       drop the held hotbar item (Q); bool All = whole stack (Ctrl+Q)
  C→S  OpenContainer/CloseContainer   client opened/closed a container block (furnace) screen
  S→C  ContainerState        open container's item slots + progress fields, streamed each tick to its viewers
  C→S  ContainerSlot         client edited one of a container block's own slots (input/fuel/output)
  S→C  PlayerStats           owning player's health/maxhealth/hunger/saturation/gamemode + dead flag; on change
  C→S  PlayerFall            completed fall distance (blocks); server applies fall damage (player owns position)
  C→S  SetGameModeRequest    pause-menu game-mode toggle (server-authoritative)
  C→S  RespawnRequest        death-screen respawn (honoured only while dead)
  S→C  DimensionChange       drop the cached world + re-enter loading; carries generic visuals (HasSky, fog, ambient)
```

**Dimensions & portals.** Each dimension is a separate `WorldServer` (see [architecture.md](architecture.md));
every `ClientSession` carries the `World` it is in, and all streaming/relay/flush loops in `ServerNetwork` are
scoped to it (`BroadcastTo(world, …)`, `session.World.LoadedChunks`, etc.). The engine owns no portal or
dimension specifics: a plugin registers an **`IDimensionPortals`** (`WorldGen/IDimensionPortals.cs`,
`GameRegistry.Portals`) defining which block is a portal, which dimension links to which, the coordinate scale,
and how to find-or-build the destination portal. Vanilla's implementation is the obsidian frame lit with flint
& steel and the 8:1 Overworld↔Nether scale.

Transfer flow (all in `ServerNetwork.Pump`): `UpdatePortals` detects a player soaking in a portal block →
`BeginTransfer` moves the player entity into the linked `WorldServer` at the scaled coords, clears the
session's `SentChunks`, and sends `DimensionChange` (the destination `Dimension`'s generic visuals). The client
parks its apply thread, drops every cached chunk/LOD/entity, switches render mode, and re-enters the loading
screen. `ProcessPendingTransfers` waits until the destination column has **generated** (`IsChunkGenerated` —
covers all-air chunks the open sky produces), then `EnsureDestinationPortal` builds/links the portal and the
server **replays the join handshake** (`LoginAccept` with the new spawn, `WorldTime`, entity sync,
`PlayerReady`) so the client finishes loading at the portal. A short post-transfer immunity (cleared when the
player steps off a portal) stops an arrival from bouncing straight back.

**Inventory is server-authoritative** (see [inventory.md](inventory.md)). The server owns each
`ClientSession.Inventory`, persists it per player, and pushes the whole thing once via `InventoryState` on
join. The client mutates its local replica optimistically for responsiveness and mirrors every change up:
`InventoryAction` (a slot edit from the creative screen) and `HeldSlot` (hotbar selection). Like placement
metadata these are trusted, not validated — this is a creative sandbox. **Dropping** is handled fully
server-side: `DropItemRequest` makes the server decrement its own copy of the held slot, spawn an
`EntityItem` thrown along the player's look direction, and **re-push the whole `InventoryState`** so the
client replica matches (the only other time `InventoryState` is sent besides login).

**Container blocks** (a furnace) are server-authoritative too, but their state lives in the block, not the
player inventory. When the client opens one it sends `OpenContainer` (pos); the server records it on the
session and, each `Pump`, streams that block's `ContainerState` (item slots + integer progress fields, read
generically from the block's `ContainerBlockData`) to its viewers, which the client copies into a per-block
`ContainerView`. Editing a furnace slot sends `ContainerSlot` (trusted, like `InventoryAction`); the player's
own inventory rows in the furnace screen still use `InventoryAction`. `CloseContainer` stops the stream. Like
`InventoryState`, `ContainerState` carries the server's live arrays by reference over loopback, so the client
**copies them by value**. The `InventoryState` packet carries
the live server `Inventory` by reference over loopback, so the client receive **copies it slot-by-slot**
(`ItemStack` is a struct) rather than aliasing the server's array.

**Survival stats are server-authoritative** (see [entities.md](entities.md)). The server runs the survival sim
on `ClientSession.Player` and, each `Pump`, `SyncPlayerStats()` sends a `PlayerStats` packet to the **owning**
client only when a value changed (cached per session). It also latches `ClientSession.Dead` from health ≤ 0.
The client mirrors the stats onto `WorldClient` (HUD, flight gate, death screen). `PlayerFall` reports a landing
so the server can apply fall damage despite the player owning its position; `SetGameModeRequest` flips the mode;
`RespawnRequest` (only while dead) resets stats + teleports the player to spawn, after which the next
`PlayerStats` clears the dead flag and the client snaps to the respawn point. Player **stats persist** alongside
the inventory in `PlayerSerializer` (`<worldDir>/Players/<name>.dat`), behind a save version + atomic
temp-and-rename write; a version mismatch resets the player rather than misreading it.

**Placement metadata is computed client-side, never read on the server.** `Block.OnPlaced(world, pos,
player, int metadata)` receives the metadata from the `PlaceBlockRequest`; the client derives it via
`Block.GetPlacementMetadata(KeyboardState, EntityPlayer, BlockRaytraceResult)` (default `0`) in
`PlayerController.PlaceBlock`. The player + ray let a block orient by the placer's look and clicked face
(stairs: facing from yaw, half from the clicked face); `BlockTintedGlass` uses only the held-key tint. The
headless server never touches input — `ServerNetwork.ApplyPlaceRequest` just passes `place.Metadata` to
`WorldServer.PlaceBlock` → `OnPlaced`. A stair's facing is block *data*, so it rides the whole-chunk
`ChunkData` resend, not the `BlockChanges` delta.

**Chunk caching & eviction is client-owned.** The client keeps every chunk it receives and, each
`WorldClient.Update`, drops chunks whose centre is farther than `CacheDistance` (240) from the player,
sending a `ChunkRelease` for each. `CacheDistance` is kept comfortably above the server's send range
(`ServerNetwork.ViewDistance`, 160) so a freshly streamed chunk is never evicted-then-re-requested at the
boundary — that hysteresis gap makes revisits free. The server's send loop only ever *adds* to `SentChunks`
(gated so a held chunk is never resent); entries leave only on `ChunkRelease` or a dirty resend. The
server-side `UnloadThread` evicts idle chunks from `WorldServer.LoadedChunks` (its own memory) — unrelated to
what the client holds.

**Edits & light propagation reach clients via per-block deltas, not whole-chunk resends.** When the server
applies an edit (`SetBlock`, tick thread) it records a `BlockChange(chunkPos, localIndex, id, light, sky)`
in `WorldServer.BlockChanges`; block-light *and* sky-light propagation (`UpdateThread`) do the same for every
node whose value **actually changed** (the BFS reads a frontier of unchanged neighbours for lookup but tracks
genuinely-changed nodes in `_lightChanged`/`_skyChanged` — only those become `BlockChange`s). `BlockChanges`
is a **`ConcurrentDictionary` keyed by absolute block (chunk pos + local index), last-write-wins**, not a
queue: rapid breaking near a torch re-lights the same cells across many overlapping floods, so deduping at
the source bounds pending changes to O(distinct changed blocks). It's correct because each `BlockChange` is a
full `(id, light, sky)` snapshot the client applies idempotently. `ServerNetwork.FlushBlockChanges()` drains
it each tick (enumerate + `TryRemove`, which terminates so a busy light thread can't trap it), groups by
chunk, and sends one compact `BlockChanges` packet per chunk to every session whose `SentChunks` holds it.
The client (`WorldClient.ApplyBlockChanges`) mutates the cached `Chunk` **in place** (`SetBlock` +
`SetLightLevel` + `SetSkyLight`, no decompress/deserialize) and remeshes only that chunk plus any **face**
neighbour a changed boundary block touches. (Edge/corner-diagonal AO seams across chunks are a pre-existing
limitation; face culling — the only correctness concern — needs only the direct face neighbour.)

**Block-data changes still ride whole-chunk resends.** `BlockChanges` carries only id + light + sky, so a
block *data* change (`SetBlockData`, e.g. tinted glass metadata, which affects `ConnectsToBlock` meshing and
`OnLightPassThrough`) marks `WorldServer.DirtyChunks` and `ServerNetwork.ResendDirtyChunks()` resends the
whole `ChunkData`. Rare; deltas handle the common place/break/light path.

`ServerNetwork.Pump()` runs once per server tick, in order: adopt pending connections → drain & handle each
session's packets (incl. `ChunkRelease`) → drop disconnected sessions → **stream chunks** (nearest-first,
in-range-not-yet-sent only, capped at `MaxChunksPerTick` per session per tick) → **send `PlayerReady`** to
any session whose `SentChunks` now covers the spawn column → relay entities → sync containers → **sync player
stats** → **flush block changes** (delta packets) → **resend dirty chunks** (block-data only).

**LOD horizon streaming.** `LodColumnData` mirrors `ChunkDataPacket` exactly — `From` carries the live region
by reference (loopback never serializes), and compression + serialization are **lazy inside `Write`/`Read`** at
the TCP boundary (a region of mostly-equal `long`s GZips hard). `ServerNetwork.StreamLodRegions` runs **after
`StreamChunks`** so it never starves detail-chunk streaming: per session, nearest-first, capped at
`MaxLodRegionsPerTick`, gated on `(playerChunk, RegionCount)` the way chunk streaming is gated on
`SentChunks`. It prunes `SentLodRegions` entries that fell out of range — but unlike chunks there is **no
client release packet**; the client evicts distant LOD on its own. Client-apply rides the same ordered apply
queue and meshes on the shared pool (see [threading.md](threading.md), [rendering.md](rendering.md)).

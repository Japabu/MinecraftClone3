# Core architecture: one client path, two server transports

Singleplayer and multiplayer share **one** client code path. Singleplayer runs the server in-process and
talks to it over an in-memory loopback connection; multiplayer swaps the loopback for a TCP socket.

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
  sky-light propagation, save/load, entity simulation. **No meshing, no GL** — runs fully headless.
- **`WorldClient`** (`Client/Blocks/WorldClient.cs`): the client replica. Holds chunks streamed from the
  server, **caches them and owns their eviction** (drops a chunk past `CacheDistance`, then sends a
  `ChunkRelease`), meshes them, renders them, holds remote entities. **No terrain gen, no disk, no lighting.**
- **`ServerNetwork`** (`Networking/ServerNetwork.cs`): per-client sessions, interest-based chunk streaming,
  dirty-chunk resends, entity relay, the TCP listener, **and the set of dimension worlds** (see below).
- **Dimensions = one `WorldServer` each.** Each dimension is its own headless `WorldServer` (its own generator,
  directory, threads, light, entities), keyed by dimension key. The primary world (`Vanilla:Overworld`) is
  created at startup; siblings (the Nether) spin up lazily the first time a player travels to one, in a
  `DIM_<key>/` subfolder. `ServerNetwork` owns them all, `TickWorlds()` ticks every one each server tick, and
  every session carries the `WorldServer` it is currently in — chunk streaming, entity relay, and block deltas
  are all scoped to `session.World`. Entity ids are process-global (static allocator) so they stay unique
  across dimensions. The engine itself bakes in no specific dimension; **portal rules + per-dimension visuals
  are content** (see [networking.md](networking.md), [worldgen.md](worldgen.md)).
- **Authority:** server owns blocks + light (block + sky). Position is **client-authoritative** — there is no
  server-side physics; the client runs walk gravity/collision and writes the result, the server relays it.
  The client *requests* edits; the server applies and broadcasts the result.
- **Join handshake (loading screen).** Login → server assigns id + a seed-derived spawn (`LoginAccept`) and
  starts streaming chunks around it → once the spawn column is *sent* it sends **`PlayerReady`** → the client
  (`StateWorld._loading`) shows a loading screen and enters the world once `PlayerReady` arrives **and** the
  spawn chunks have decoded locally (ground before gravity). One packet path, so SP (loopback) and MP (TCP)
  share the exact same flow; a wall-clock timeout only fail-safes a dropped signal.
- **Dimension transfer reuses the join handshake.** When a player soaks in a portal block, the server moves
  their entity into the linked dimension's `WorldServer` at the scaled coordinates and sends a
  **`DimensionChange`** (carrying the destination's generic visuals — sky on/off, fog colour, ambient floor).
  The client tears its whole cached world down and re-enters the loading screen; once the destination column
  streams in server-side, the server builds/links the portal and replays `LoginAccept` + `PlayerReady` so the
  client finishes loading at the new portal. Same one packet path for SP and MP.
- **Chunk lifetime is client-owned (see [networking.md](networking.md)).** The server streams a chunk once and
  keeps it in the session's `SentChunks` until the chunk is dirtied or the **client** releases it; the server
  never tells a client to unload. Walk away and back and the client re-renders from its own cache — zero bytes
  on the wire.

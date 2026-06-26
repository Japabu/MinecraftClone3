# Core architecture: one client path, two server transports

## Assembly boundary: GL-free Core vs. the Client renderer

The engine is split into two libraries so the "server never touches GL" rule is **compiler-enforced**:

- **`MinecraftClone3API`** (Core) — GL-free. Block storage/logic, worldgen, entities, networking, plugins, IO,
  and the GL-free CPU model/mesh data (`BlockModel`, `BlockTexture`, `MeshBuffer`, the CPU half of
  `BlockTextureManager`, `ChunkMesher`). References OpenTK for **math only**. The dedicated server links *only*
  this — so a server-reachable path that reaches for the renderer or ambient input/window state fails to
  compile.
- **`MinecraftClone3API.Client`** — the GL renderer, GUI, input (`PlayerController`), the GL halves of the
  resource readers (`GlResources`/`GlTextureUploader`), and `WorldClient`. References Core.

Both keep the **same root namespaces** (`MinecraftClone3API.*`); the assembly a file compiles into is
independent of its namespace, so a type can move across the boundary with no `using` changes. Core grants
`[InternalsVisibleTo("MinecraftClone3API.Client")]` so the renderer reuses Core internals (mesher, chunk
codec) — a one-way grant: Core never references Client, and the server (a separate assembly without the grant)
gets nothing. Cross-boundary client→Core data that Core needs to log (the profiler's per-frame world/GPU
samples) is **pushed** in via `ClientFrameStats`/`ClientProfiling` rather than pulled, keeping `Profiler`
itself in Core. The client exe links Client; `VanillaPlugin` links Client too (its furnace/crafting blocks
open GUIs) but the server loads it reflectively and never invokes those GL members, so the Client assembly
never reaches the server's binary.

## One client path, two server transports

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
- **`WorldClient`** (`MinecraftClone3API.Client/Blocks/WorldClient.cs`): the client replica. Holds chunks streamed from the
  server, **caches them and owns their eviction** (drops a chunk past `CacheDistance`, then sends a
  `ChunkRelease`), meshes them, renders them, holds remote entities. **No terrain gen, no disk, no lighting.**
- **`ServerNetwork`** (`Networking/ServerNetwork.cs`): per-client sessions, interest-based chunk streaming,
  dirty-chunk resends, entity relay, the TCP listener.
- **Authority:** server owns blocks + light (block + sky). Position is **client-authoritative** — there is no
  server-side physics; the client runs walk gravity/collision and writes the result, the server relays it.
  The client *requests* edits; the server applies and broadcasts the result.
- **Join handshake (loading screen).** Login → server assigns id + a seed-derived spawn (`LoginAccept`) and
  starts streaming chunks around it → once the spawn column is *sent* it sends **`PlayerReady`** → the client
  (`StateWorld._loading`) shows a loading screen and enters the world once `PlayerReady` arrives **and** the
  spawn chunks have decoded locally (ground before gravity). One packet path, so SP (loopback) and MP (TCP)
  share the exact same flow; a wall-clock timeout only fail-safes a dropped signal.
- **Chunk lifetime is client-owned (see [networking.md](networking.md)).** The server streams a chunk once and
  keeps it in the session's `SentChunks` until the chunk is dirtied or the **client** releases it; the server
  never tells a client to unload. Walk away and back and the client re-renders from its own cache — zero bytes
  on the wire.

# CLAUDE.md

A from-scratch Minecraft-like voxel engine in C# on OpenTK (OpenGL). Custom deferred renderer with a
Distant-Horizons-style far-terrain LOD horizon, plugin system, chunked world with RGB block-light +
sky-light propagation and a dynamic day/night cycle, a
plugin-extensible world generator (biomes, ores, trees, caves, oceans), player movement (MC-exact walking +
a creative free-flight toggle), and client/server multiplayer (singleplayer runs an in-process server over a
loopback connection; multiplayer connects to a dedicated server over TCP).

> **⚠️ KEEP THE DOCS UP TO DATE — and keep THIS file lean.** This root is always loaded into context, so it
> holds only the orientation map, the cross-cutting rules below, and an index into `docs/`. **The deep detail
> for each subsystem lives in `docs/*.md`, read on demand.** When you change a subsystem, update its doc in
> the same change; only add to this file a rule that applies to *every* change. Describe how the project works
> *now* — not a changelog of past fixes (no dates, no trace percentages, no "used to be X"). A stale doc is
> worse than none. Treat editing the docs as part of "done," not an afterthought.

---

## Solution layout

```
MinecraftClone3.sln
├── MinecraftClone3API      Shared library — ALL engine logic lives here.
│   ├── Blocks/             WorldBase, WorldServer, Chunk (storage), CachedChunk, Block, LightLevel, PaletteStorage
│   ├── Client/             Client-only code (needs a GL context)
│   │   ├── Blocks/         WorldClient (client world replica)
│   │   ├── Graphics/       WorldRenderer, ChunkRenderData, EntityRenderer, VAOs, Camera, RenderDebug
│   │   ├── GUI/            GuiBase, GuiButton, GuiSlider, GuiTextInput, Font, widgets
│   │   └── StateSystem/    StateEngine, StateBase, GuiBase
│   ├── Entities/           Entity, EntityPlayer, PlayerController, PlayerPhysics
│   ├── IO/                 GamePaths, WorldManager (+WorldInfo), FileSystem*, ResourceReader, CommonResources
│   ├── Networking/         IConnection, Packet(s), Loopback/Tcp connections, ServerNetwork, ClientSession
│   ├── Plugins/            PluginManager, IPlugin, PluginContext
│   ├── WorldGen/           Dimension, Biome, Feature, Carver, BiomeSource, NoiseChunkGenerator, region, RNG
│   └── Util/               GameRegistry, BlockRegistry, ChunkMesher, WorldSerializer, OpenSimplexNoise, Profiler
├── MinecraftClone3         Client executable (OpenTK GameWindow, ~120 Hz). Owns Program + States/.
├── MinecraftClone3Server   Dedicated headless server executable (no GL).
└── VanillaPlugin           Content plugin: blocks (Stone, Sand, OakLog, Water, ores, ...) + the Overworld
                            dimension, biomes, ore/tree features (VanillaPlugin/WorldGen/).
```

`MinecraftClone3` and `MinecraftClone3Server` are thin shells; nearly everything is in the API library.
Target framework **net10.0**. `<Nullable>` and `<ImplicitUsings>` are **disabled** — write explicit `using`s
and don't rely on nullable annotations.

---

## Build & run

```bash
dotnet build MinecraftClone3.sln -c Debug          # build everything
dotnet run --project MinecraftClone3 -c Debug       # run the client (needs a DISPLAY / GL context)
dotnet run --project MinecraftClone3Server -c Debug # run the dedicated server (headless, Ctrl-C to save+stop)
```

The server listens on **127.0.0.1:25565** (`ServerNetwork.DefaultPort`); the client's multiplayer button
connects there. **Singleplayer worlds** each live under `~/.local/share/MinecraftClone3/Worlds/<name>/`
(created/listed/deleted on the world-selection screen); the **dedicated server** uses one fixed
`~/.local/share/MinecraftClone3/World/`. Each world's name, seed, and last-played time persist to
`<worldDir>/level.dat` (`WorldMetadata`). Block textures come from a resource pack (a 1.13+ Minecraft client
jar) dropped in `~/.local/share/MinecraftClone3/ResourcePacks/`; with none, blocks render with placeholders.

Runtime verification (running the game, capturing traces) is the maintainer's job — see Conventions.
`dotnet build` is the check you run.

---

## Invariants & gotchas — apply to ANY change

These are the cross-cutting rules you can break *without realizing it*. The full rationale for each lives in
the linked doc; this is the short "don't violate this" list.

**Threading & concurrency** (full: [docs/threading.md](docs/threading.md))
- **GL only on the main thread.** Even *constructing* a `ChunkRenderData` is a GL call (`GL.GenVertexArray`).
  Meshing (`ChunkRenderData.Update`) is CPU-only and runs on the mesh pool; render-data creation, `Upload`,
  `Draw`, `Dispose` are main-thread.
- **`ChunkRenderData.TryUpload()` is gated on `Updated` and must stay non-blocking** (`Monitor.TryEnter`, not
  a blocking `Upload`). The mesh thread holds the VAO locks for a whole remesh; a blocking upload stalled the
  render thread. Don't reintroduce one.
- **Per-`PaletteStorage`-container single writer + copy-on-grow** (full: [docs/world-model.md](docs/world-model.md)).
  A published storage's palette/bit-width are immutable; growth publishes a NEW storage via a `volatile`
  field. Each container (block ids / light / sky) has exactly one writer thread. **Never add a second writer.**
- **Block-id agreement.** Block ids come from plugin **enumeration order** at load; client and server MUST
  load the same `Plugins/`.

**Architecture** (full: [docs/architecture.md](docs/architecture.md), [docs/world-model.md](docs/world-model.md))
- **Storage vs. mesh are decoupled.** `Chunk` is pure GL-free storage (the headless server builds chunks);
  the GPU mesh is a separate client-only `ChunkRenderData`. Don't merge them.
- **One client path, two transports** — loopback (SP) / TCP (MP). Keep them behaviourally identical; only the
  in-process transport shortcuts serialization.
- **Authority:** the server owns blocks + light; the **player's** position is client-authoritative (no
  server-side *player* physics — the client runs walk gravity/collision and writes the result). Every **other**
  entity (mobs/animals/dropped items) is server-authoritative — the server runs its AI/physics and streams it
  (see [docs/entities.md](docs/entities.md)).
- **Chunk lifetime is client-owned.** The server streams a chunk once and never tells a client to unload; the
  client caches and releases (`ChunkRelease`).

**Operational / formats**
- **After any worldgen, on-disk, or wire-format change, delete the affected world folder** (`Worlds/<name>/`,
  or `World/` for the dedicated server). Chunks load disk-first, so old saves mask the new generator.
- **OpenGL is capped at 4.1 Core / GLSL 4.10** (macOS limit): query uniform/sampler locations by name (no
  `layout(binding=)` on uniforms); vertex-attribute/fragment-output locations *do* use `layout(location=)`.
- **Block code that runs on the server must not touch client/GL/window state** (server-side light sim calls
  `Block.GetLightLevel`; reading the keyboard there crashed `BlockTorch`).

---

## Conventions

- **Comments:** self-documenting code. Only `///` XML doc comments where they earn their place — **no inline
  `//` narration** of what the next line does.
- Match the surrounding code's style, naming, and comment density.
- **No backwards compatibility.** Rapid development, no shipped users. Do **not** add format-version
  negotiation, save migrations, deprecation shims, or compatibility fallbacks. When a format changes, the
  world is regenerated (delete the folder). Prefer the clean break. (Crash-robustness — e.g. regenerating a
  truncated/corrupt chunk rather than killing the load thread — is fine; that is not back-compat.)
- **Record, don't interrupt.** When working a multi-step task, do **not** run the game, capture traces, or
  pause for the maintainer to verify between steps — the maintainer tests at the end. If you spot a bug, risk,
  or improvement along the way, **write it into `docs/known-issues.md`** instead of stopping. `dotnet build`
  is fine; runtime verification is the maintainer's.
- **Flag context rot — don't push through it.** If the conversation has grown long enough that earlier detail
  is blurring (re-deriving things, losing track of decisions), **stop and tell the maintainer it's a good time
  to `/compact`** rather than soldiering on with degrading context.
- **Keep the docs lean.** When you finish a subsystem change, update its doc — but prune as you go: a resolved
  "known issue" is deleted, not annotated; rationale lives in the permanent doc, never as history.

---

## Where the detail lives — read the relevant doc before working on a subsystem

| Working on… | Read |
|---|---|
| Client/server split, transports, join handshake, authority | [docs/architecture.md](docs/architecture.md) |
| Chunk/`PaletteStorage` storage, meshing contract, copy-on-grow rule | [docs/world-model.md](docs/world-model.md) |
| Terrain gen, biomes, features, carvers, determinism | [docs/worldgen.md](docs/worldgen.md) |
| Packets, chunk streaming, block-change deltas, caching/eviction | [docs/networking.md](docs/networking.md) |
| Threads, the tick loop, the 5 invariants in full | [docs/threading.md](docs/threading.md) |
| Deferred renderer, shadows, sky/water shaders, the G-buffer, the Phase-2 distant-horizon LOD | [docs/rendering.md](docs/rendering.md) |
| States/overlays, the game loop, player physics, graphics options | [docs/state-gameloop.md](docs/state-gameloop.md) |
| Mobs/animals/dropped items/remote players: types, server AI, box models, entity rendering | [docs/entities.md](docs/entities.md) |
| Items/registry, inventory, hotbar HUD, creative screen, 3D icons, crafting (recipes + 2x2/3x3) | [docs/inventory.md](docs/inventory.md) |
| Resource cascade, plugin loading, resource packs, models/textures | [docs/resources.md](docs/resources.md) |
| F1/F3/F4/F7/F10 debug keys, the profiler CSVs, dotnet-trace, RenderDoc | [docs/profiling.md](docs/profiling.md) |
| Hot-path code — why it's shaped that way (**don't regress these**) | [docs/performance.md](docs/performance.md) |
| Open work, deferred features, accepted limitations | [docs/known-issues.md](docs/known-issues.md) |

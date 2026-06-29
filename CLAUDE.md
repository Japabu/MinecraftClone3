# CLAUDE.md

A from-scratch Minecraft-like voxel engine in C# on WebGPU (Silk.NET.WebGPU → wgpu-native → Metal on macOS,
Vulkan on Linux, D3D12 on Windows; windowing/input via Silk.NET). Custom GPU-driven deferred renderer (a
compute shader frustum/distance-culls chunks into an indirect-multidraw buffer) with HDR + reverse-Z, a
Distant-Horizons-style far-terrain LOD horizon, plugin system, chunked world with RGB block-light +
sky-light propagation and a dynamic day/night cycle, a
plugin-extensible world generator (biomes, ores, trees, caves, oceans), player movement (MC-exact walking +
a creative free-flight toggle), server-authoritative survival (health/hunger, fall/void/drowning damage,
death/respawn, eating, per-player game-mode toggle), and client/server multiplayer (singleplayer runs an
in-process server over a loopback connection; multiplayer connects to a dedicated server over TCP).

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
├── MinecraftClone3API        Core engine library — GPU-FREE (no graphics API; Silk.NET.Maths only). Server links only this.
│   ├── Blocks/               WorldBase, WorldServer, Chunk (storage), CachedChunk, Block, PaletteStorage
│   ├── Entities/             Entity, EntityPlayer, PlayerPhysics (NOT PlayerController — that's client input)
│   ├── Graphics/             GPU-free CPU model/mesh data: BlockModel, BlockStateDefinition, BlockTexture,
│   │                         TextureData, MeshBuffer, BlockTextureManager (CPU half), VaoBufferPool
│   ├── IO/                   GamePaths, WorldManager (+WorldInfo), FileSystem*, ResourceReader (GPU-free), CommonResources
│   ├── Items/                Item, ItemStack, ItemBlock, ItemUnknown, Inventory, CraftingRecipe, RecipeLoader, CreativeTab
│   ├── Networking/           IConnection, Packet(s), Loopback/Tcp connections, ServerNetwork, ClientSession
│   ├── Plugins/              PluginManager, IPlugin, PluginContext
│   ├── WorldGen/             Dimension, Biome, Feature, Carver, BiomeSource, NoiseChunkGenerator, region, RNG
│   └── Util/                 GameRegistry, BlockRegistry, ChunkMesher, WorldSerializer, LightLevel, OpenSimplexNoise, Profiler, ClientFrameStats
├── MinecraftClone3API.Client Client renderer library (needs a GPU + window). References Core.
│   ├── Blocks/               WorldClient (client world replica)
│   ├── Graphics/             WorldRenderer, Renderer (frame conductor), ChunkRenderData, ChunkMeshArena + ChunkCuller
│   │                         (GPU-driven cull), EntityRenderer, Camera, RenderDebug, GlResources/BlockTextureUploader
│   │                         (GPU halves of the resource readers)
│   │   └── Rhi/              WebGPU wrappers (all `unsafe` Silk.NET.WebGPU interop): Gpu/GpuContext, GpuBuffer,
│   │                         GpuTexture/GpuSampler, GpuShaderModule, Gpu{Render,Compute}Pipeline, bind groups, passes
│   ├── GUI/                  GuiBase, GuiButton, GuiSlider, GuiTextInput, Font, widgets
│   ├── StateSystem/          StateEngine, StateBase, GuiBase
│   ├── Entities/             PlayerController (client input/camera)
│   └── Util/                 Benchmark, Inspect, ClientProfiling (per-frame sampler)
├── MinecraftClone3           Client executable (Silk.NET window + input, ~120 Hz). Owns Program + States/. Links Client.
├── MinecraftClone3Server     Dedicated headless server executable (no GPU). Links Core only.
└── VanillaPlugin             Content plugin: blocks (Stone, Sand, OakLog, Water, ores, ...) + the Overworld
                              dimension, biomes, ore/tree features (VanillaPlugin/WorldGen/). Links Client (GUI blocks).
```

`MinecraftClone3` and `MinecraftClone3Server` are thin shells; nearly everything is in the two API libraries.
**The Core/Client split makes the "server code must not touch the GPU" rule compiler-enforced**: Core is
GPU-free (Silk.NET.*Maths* only, no Silk.NET.WebGPU/Windowing/Input), the WebGPU renderer lives in
`MinecraftClone3API.Client`, and the server links only Core — so a server-reachable path that reaches for
GPU/window state fails to compile. Core grants
`[InternalsVisibleTo("MinecraftClone3API.Client")]` so the renderer keeps using Core internals (mesher, chunk
codec); this is one-way (Core never sees Client, the server never gets the grant).
Both API assemblies keep the **same root namespaces** (`MinecraftClone3API.*`, incl. the `MinecraftClone3API.Graphics`
model-data types that live in Core) — the assembly a file lives in is decoupled from its
namespace, so moving a file across the boundary needs no `using` changes.
Target framework **net10.0**. `<Nullable>` and `<ImplicitUsings>` are **disabled** — write explicit `using`s
and don't rely on nullable annotations.

---

## Build & run

```bash
dotnet build MinecraftClone3.sln -c Debug          # build everything
dotnet run --project MinecraftClone3 -c Debug       # run the client (needs a GPU + window)
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
- **GPU work is on the main thread.** Meshing (`ChunkRenderData.Update`) is CPU-only on the mesh pool;
  render-data `Upload`/`Draw`/`Dispose` and the per-frame surface + encoder + `Queue.Submit` are main-thread.
  (The WebGPU queue + resource creation are thread-safe in wgpu-native, so moving chunk *upload* off the main
  thread is a planned level-up — see `docs/known-issues.md` — but the current model is main-thread upload.)
- **`ChunkRenderData.TryUpload()` is gated on `Updated` and must stay non-blocking** (`Monitor.TryEnter`, not
  a blocking `Upload`). The mesh thread holds the opaque buffer + transparent-VAO locks for a whole remesh, so
  a blocking upload would stall the render thread.
- **Per-`PaletteStorage`-container single writer + copy-on-grow** (full: [docs/world-model.md](docs/world-model.md)).
  A published storage's palette/bit-width are immutable; growth publishes a NEW storage via a `volatile`
  field. Each container (block ids / light / sky) has exactly one writer thread. **Never add a second writer.**
- **Block/item ids are session-local; disk + wire identity is the registry NAME.** Numeric ids are assigned
  at load (deterministic plugin order) and only need to agree within a session — so the client and server MUST
  load the same `Plugins/`. Chunks/inventories persist and transmit the stable `RegistryKey` (name), remapped
  to ids on read; a name whose plugin is gone is preserved as an inert `BlockUnknown`/`ItemUnknown` placeholder
  (re-installing the plugin restores it). So adding/removing/reordering blocks/items never corrupts a world.

**Architecture** (full: [docs/architecture.md](docs/architecture.md), [docs/world-model.md](docs/world-model.md))
- **Storage vs. mesh are decoupled.** `Chunk` is pure GPU-free storage (the headless server builds chunks);
  the GPU mesh is a separate client-only `ChunkRenderData`. Don't merge them.
- **One client path, two transports** — loopback (SP) / TCP (MP). Keep them behaviourally identical; only the
  in-process transport shortcuts serialization.
- **Authority:** the server owns blocks + light; the **player's** position is client-authoritative (no
  server-side *player* physics — the client runs walk gravity/collision and writes the result). Every **other**
  entity (mobs/animals/dropped items) is server-authoritative — the server runs its AI/physics and streams it
  (see [docs/entities.md](docs/entities.md)). Entities persist with their chunk and the player persists via
  `PlayerSerializer`; all `Entities` mutation (spawn/save/despawn) stays on the **tick thread**.
- **Chunk lifetime is client-owned.** The server streams a chunk once and never tells a client to unload; the
  client caches and releases (`ChunkRelease`).

**Operational / formats**
- **The save format is versioned and self-describing within its version.** `level.dat`/player files carry a
  save version and region files a magic+version header; a mismatch is rejected and regenerated (fail-fast, no
  back-compat). Within a version, blocks/items survive plugin churn (name-based, above). So a **worldgen**
  change still needs the world folder deleted (chunks load disk-first, masking the new generator); a
  **format** change should instead **bump the version** (`WorldMetadata.Version` / `WorldSerializer`'s
  `ChunkVersion`/`EntityVersion` region headers / `PlayerSerializer.Version`) so old saves are cleanly rejected rather than misread. Writes are atomic
  (temp + rename); region `.rd` files self-compact to reclaim re-saved chunks; dirty chunks + players autosave
  on a timer (not only on unload/shutdown).
- **Shaders are WGSL; the renderer targets WebGPU via wgpu-native** (Metal on macOS). Reverse-Z with z ∈ [0,1]
  clip (depth cleared to 0, compare `Greater`); matrices upload row-major straight into WGSL's column-major
  (that copy *is* the transpose — see `Rhi/MatrixConvert`). `MultiDrawIndexedIndirectCount` and push constants
  are wgpu-native extensions (feature-gated in `GpuFeatures`). Uniforms are grouped bind groups (group 0
  per-frame, group 1 per-pass, group 2 textures/samplers) + push constants / dynamic-offset UBOs for per-draw;
  UBO field layout follows WGSL std140 (`vec3`+`f32` packs to 16 B), so C# UBO structs must mirror it exactly.
- **Block code that runs on the server must not touch client/GPU/window state** (server-side light sim calls
  `Block.GetLightLevel`, which must not read client input/keyboard). **Fully compiler-enforced**: Core
  (which the server links) cannot reference the WebGPU renderer or the ambient input/window/`RenderDebug`
  statics — those live only in `MinecraftClone3API.Client`, and `Block`'s client-facing virtuals
  (`GetPlacementMetadata`, `OnActivated`) take only Core types (`EntityPlayer`/`BlockRaytraceResult`/
  `WorldBase`), so no graphics/input type leaks into Core at all.

---

## Conventions

- **Comments are the exception, not the default.** Self-documenting code first: small functions, precise
  names, readable structure. A comment is justified only when it explains a **why the code itself cannot** — a
  non-obvious invariant, a gotcha, an external constraint (a spec, a hardware/driver quirk, a "looks redundant
  but isn't" trap). These are most valuable over code that looks *simple and innocent* but has an invisible
  reason. **Never narrate *what* the next line does** (`// loop over chunks`, `// increment i`) — delete on
  sight. And if the urge to comment comes from code being *hard to follow*, **fix the code instead** (extract,
  rename, simplify); a comment over confusing code is a band-aid that rots. `///` XML doc comments only where
  they earn their place and stay concise — a `///` that just restates the signature (`/// Gets the name`) is
  the same noise. No changelog narration in comments (no "used to", "now", "previously", dates, trace %).
- Match the surrounding code's comment density (which should be low).
- **Work in a worktree — one session, one worktree.** Do every feature/fix in a git worktree created with the
  dedicated worktree tool (`EnterWorktree`), never a manual `git worktree add`. **One chat session uses exactly
  one worktree**, so independent sessions run in parallel without stepping on each other; a single worktree may
  bundle several features. Worktrees branch from **local `master`** (the repo sets `worktree.baseRef=head`
  because `origin/master` can lag local). A worktree is **merged into `master` and then deleted** only once the
  work is done, `dotnet build` is clean, **and the maintainer has tested and reviewed it** — never merge
  unreviewed/untested.
  - **Edit the worktree's copy, not the main repo's.** The IDE "opened file" context and sub-agents (Explore,
    etc.) report absolute paths rooted at the *original* repo (`/…/MinecraftClone3/<sub>`); editing those
    verbatim silently writes to the `master` checkout, not the worktree, because only the path prefix differs.
    A `dotnet build` from the worktree still passes (it compiled the unchanged worktree), hiding the mistake.
    Rewrite any handed-in path to the worktree prefix (`/…/MinecraftClone3/.claude/worktrees/<name>/<sub>`)
    before Read/Edit/Write, and confirm edits landed with `git -C <worktree> status`, not just a green build.
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
| Reading 1:1 behavior/models/recipes out of the user's MC jar (assets + cfr-decompiling hardcoded Java) | [docs/minecraft-reference.md](docs/minecraft-reference.md) |
| F1/F3/F4/F7/F10 debug keys, the profiler CSVs, dotnet-trace, GPU frame capture | [docs/profiling.md](docs/profiling.md) |
| Hot-path code — why it's shaped that way (**don't regress these**) | [docs/performance.md](docs/performance.md) |
| Open work, deferred features, accepted limitations | [docs/known-issues.md](docs/known-issues.md) |

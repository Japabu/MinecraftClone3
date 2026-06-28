# World generation: an engine framework, vanilla owns the content

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
the block registries; `PluginContext.Register(Biome|Feature|Dimension)` prefixes with the plugin id like
`Register(Block)`. All three are `RegistryEntry` (`prefix:name` keys). Lifecycle: plugins register **blocks
→ features → biomes → the dimension in `Load`**, then attach dimension-shared features (the 4 ore veins) in
**`PostLoad`** (so other plugins' biomes/features already exist). `WorldServer` is constructed after all
`PostLoad`, so `Dimension.CreateGenerator` sees a complete registry — its `ClimateBiomeSource` enumerates
every biome tagged `Vanilla:Overworld`, so a plugin biome auto-participates.

**Per-chunk pipeline (`NoiseChunkGenerator.Generate`, no neighbour block reads):**
1. **Biome + surface-height map** for the chunk's 16×16 columns (reused scratch). Biome is climate
   (temp/humidity Voronoi) for land, with **height-derived overrides**: base height well below sea → Ocean,
   shoreline band → Beach. Surface height = base noise + **blended** `HeightBias` + peaks·**blended**
   `HeightVariation`: `SurfaceHeight` bilinearly interpolates the four surrounding biomes' `HeightBias`/
   `HeightVariation` over a world-aligned lattice (spacing `HeightBlendSpacing`, smoothstep weights), so a
   biome border (Mountains↔Plains) is a **foothill, not a cliff**. The blend is a pure function of (wx,wz) —
   `_surf` holds it and *all* height consumers use it (fill, carver carve-ceiling, trees, `Spawn`). Surface
   *blocks* still snap to the hard per-column biome (`_colBiome`); only *height* blends (MC pre-1.18).
2. **Base terrain** — bedrock at `BedrockY`, stone up to the surface.
3. **Surface skin** — biome `TopBlock`/`FillerBlock` above sea, `UnderwaterBlock` (sand/gravel) below.
4. **Water** — `Vanilla:Water` fills air below `SeaLevel` on ocean columns.
5. **Carvers** — `NoiseCaveCarver` overwrites stone with air below the surface skin (never bedrock).
6. **Sky seed** — open air above the surface → sky 15; the water column dims one level per block of depth;
   carved caves keep sky 0. Preserves the `IsEmpty` fast path (above-terrain air chunks only `SetSkyLight`,
   stay unstreamed).
7. **Decoration** — for the chunk and each origin in a **±1-chunk XZ margin**, seed a `WorldGenRandom` from
   `(seed, originChunk, feature.Salt)` and run the dimension's shared features (ores) then the origin's
   centre-biome features (trees + ground cover) for each `DecorationStep` (Ores, Vegetation). Ground cover is
   `PatchFeature` — scatters a single-block plant (grass tuft/fern/flower) in small clusters on exposed grass
   columns above sea level; reach is bounded by the patch radius so it stays inside the margin. Features emit in **absolute
   coordinates through `IChunkGenRegion`, which clips writes to the chunk being generated** — so a tree or
   vein straddling a border is computed identically by both chunks (Minecraft's population-seed model) with
   **no neighbour writes**. Recomputed per vertical chunk too (the RNG is Y-independent), so a feature
   crossing a vertical boundary is stamped consistently.

**Determinism & threading.** Seeds are **process-stable**: `OpenSimplexNoise` is a seeded instance
(Fisher–Yates perm from a SplitMix64 stream), `WorldGenRandom` is a struct SplitMix64 PRNG, and
`Feature.Salt` is an **FNV-1a** hash of the registry key (never `string.GetHashCode`, which is per-run
randomized). `Generate` runs only on the server **LoadThread** (single writer — Invariant 5). The **seed
persists** to each world's `level.dat`; both call sites construct `new WorldServer(long seed, string
worldDir)` — `StateWorld` SP with `WorldInfo.Seed`/`Directory`, `MinecraftClone3Server` via
`WorldMetadata.LoadOrCreate`. `WorldServer` owns a per-instance `WorldSerializer(worldDir)` (safe: exactly
one `WorldServer` per process). The generator resolves `GameRegistry.GetDimension("Vanilla:Overworld")`; if
absent it logs and falls back to a `FlatChunkGenerator` (empty void). **Generation is server-only** — clients
receive baked chunks.

**LoadThread band.** The interest scan loads the **full `MinChunkY..MaxChunkY` vertical column** within
`TerrainRadius` (10 chunks, matched to `ServerNetwork.ViewDistance`/`RenderDistance`) around each player —
the world has real vertical extent (oceans, caves, mountains). Per-tick cost is bounded (16-chunk cap,
distance sort, dedup); resident chunk count grows (tune `TerrainRadius`/`ChunkLifetime`).

**Spawn** comes from `NoiseChunkGenerator.Spawn()` (spiral out from origin for the first land column);
`ServerNetwork` caches it and seeds `LoginAccept`.

**Dimensions beyond the Overworld.** A `Dimension` is just a `RegistryEntry` whose `CreateGenerator(seed)`
returns any `IChunkGenerator` — it need not be the `NoiseChunkGenerator`. Vanilla's `NetherDimension`
(`VanillaPlugin/WorldGen/`) wires a bespoke `NetherChunkGenerator`: a 128-tall netherrack slab between a
bedrock floor (y 0) and ceiling (y 127), hollowed by 3D-noise caverns, with a lava sea below `LavaLevel` (31),
soul-sand cavern floors, and glowstone clusters on cavern ceilings. It is pure/thread-safe like the noise
generator (read-only noise + per-cell decisions, no neighbour reads — decoration samples the pure `BaseAt`).
Crucially it **never seeds sky light**, so the sealed slab stays dark (lit only by lava/glowstone/portals);
the bedrock shell stops the sky-light scan from leaking in. Each dimension is its own `WorldServer`
(see [architecture.md](architecture.md)); the Nether spins up lazily on first portal travel.

`Dimension` also carries **generic client visuals** — `HasSky`, `FogColor`, `AmbientLight` — that the engine
ships to the client on a dimension change (the engine knows no dimension specifics; content sets these).
`HasSky=false` drops the sun/day-night/stars and paints a flat `FogColor`; `AmbientLight` is a minimum light
flooded everywhere so a sunless dimension isn't pitch black (see [rendering.md](rendering.md)). The Nether sets
a dark red fog + a dim red ambient; the Overworld keeps the defaults (sky on, no overrides).

**Server LOD generation.** For the distant horizon (the `LodColumn`/`LodColumnStore` data model lives in
[world-model.md](world-model.md)), the server fills a region surface-only — no full 16³ chunk, no carving, no
light. `NoiseChunkGenerator.GetLodColumn` computes one column's topmost visible surface (land `TopBlock` /
ocean water) directly from the existing pure noise/biome functions (a couple of noise evals), so it is far
cheaper than `Generate`. `DecorateLodRegion` then stamps tree canopies: it replays the vegetation feature RNG
**once per region** (not per column) via `TreeFeature.CollectTrees` — the shared replay entry point —
producing the exact same draws as `TreeFeature.Place` (bit-identical RNG order: chance → x → z →
sea-level-skip → height), and raises the covered stride representative columns to the canopy top with
`StampCanopy`. So the far-horizon forests land on the **same positions as the real trees**, with no tree pop
at the render-distance boundary.

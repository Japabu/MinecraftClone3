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
   centre-biome features (trees) for each `DecorationStep` (Ores, Vegetation). Features emit in **absolute
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

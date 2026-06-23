# World & chunk model: storage vs. mesh are decoupled

`Chunk` is pure GL-free storage so the headless server can construct chunks. The GPU mesh lives in a
separate client-only `ChunkRenderData`. This split is the backbone of the whole design.

```
            WorldBase (abstract: coords, raytrace, Get/SetBlock contract)
            /                                    \
     WorldServer                              WorldClient
     - LoadedChunks: Chunk (storage)          - LoadedChunks: Chunk (received copies)
     - terrain gen / light / save             - RenderData: Vector3i -> ChunkRenderData (GL mesh)
     - 3 background threads                    - mesh-thread pool + 1 apply thread

   Chunk  (Blocks/Chunk.cs)            ChunkRenderData  (Client/Graphics/ChunkRenderData.cs)
   - PaletteStorage block ids          - holds a Chunk + two VertexArrayObjects (opaque + transparent)
   - PaletteStorage light (RGB)        - Update() : CPU meshing (ChunkMesher) — safe off-thread
   - PaletteStorage sky (0..15)        - Upload()/Draw()/Dispose() : GL — main thread ONLY
   - block data, min/max bounds        - Upload() gated on `Updated` (see invariants)
   - Write(BinaryWriter)
```

**Storage is bit-packed paletted, not dense arrays** (`Blocks/PaletteStorage.cs`). A `Chunk` holds three
`PaletteStorage` containers (block ids, packed RGB light, sky light) plus the block-data dict and min/max.
Each container is a palette of the distinct values + a bit-packed index array (`bitsPerEntry =
ceil(log2(count))`, or a single-value fast path with no index array for a uniform chunk). The block-light
container is single-value (all-zero) away from torches; the sky container is single-value (all-zero)
underground and carries a small `{0,15}` palette only at the surface (and dug caves). This shrinks the
per-chunk clone **and** the resident chunk heap ~10–50× versus dense arrays. The `Chunk.Index` x/y/z
flattening order defines the layout, so the (de)serializers must iterate in that order.

All-air chunks above terrain *are* seeded all-15 but stay `IsEmpty` and are never streamed (the client
falls back to sky 15 for any unloaded chunk). `SetSkyLight` deliberately does **not** expand min/max (sky
fills air everywhere; expanding would defeat the single-value fast path, blow up the mesher's `min..max`
loop, and break `IsEmpty`).

`ChunkMesher.AddBlockToVao(WorldBase, ...)` reads neighbour blocks through `WorldBase`, so it works for any
world. It meshes each of `Block.Model.Elements` (Minecraft `from`/`to` boxes with per-face `uv`/texture/
`cullface`), so partial-cube models (stairs) render as-is. **Per-block orientation** is
`Block.GetModelTransform` (default identity), composed after the element transform so it rotates the centred
element about the block origin — the engine parses **no blockstate files**, so a stair's facing is applied
here from the block's stored metadata. (Face normals + `cullface` are not rotated — harmless: a partial
block is never the both-full pair the face cull needs, so its faces always draw; only flat shading on
rotated faces is mildly off.) Chunk serialization (`Chunk.Write` ↔ `new CachedChunk(world, pos, reader)`) is
reused for both disk saves (`WorldSerializer`) and the `ChunkData` packet; both go through
`PaletteStorage.Write`/`Read`.

**Paletted storage is concurrency-safe by a single-writer + copy-on-grow rule** (see `PaletteStorage`'s
class doc). A published storage's palette and bit-width are immutable; a `Set` reusing an existing value
rewrites one packed entry in place (a benign single-entry torn read, as the old dense `ushort[]` already
tolerated), while a `Set` introducing a new value returns a NEW storage the chunk publishes through its
`volatile` field. Each container has exactly one writer thread (server: block ids = tick thread, light + sky
= light/Update thread, plus the LoadThread seeds sky at gen before publish; client: all three = apply
thread), so concurrent readers (mesher, network serialize, raytrace) always see a structurally consistent
snapshot. **Do not introduce a second writer to any container.**

## LOD columns: the distant-horizon storage model

The Phase-2 distant horizon streams a second, far cheaper representation for terrain *beyond* the render
distance (see [worldgen.md](worldgen.md) for how the server generates it). A `LodColumn`
(`Blocks/LodColumn.cs`) is one **region** — a 128-block XZ footprint — of **surface-only** columns sampled at
a stride (stride-2 is the store's finest), each column a single packed `long` (block id, surface Y, sky
light). It is **Y-agnostic**: no full 16³ chunk, no light volume, no palette — the store *is* the heightmap at
render stride.

A filled region's `Columns` array is **immutable once published** — a re-fill replaces the whole `LodColumn`
object rather than mutating it in place. This makes the loopback by-reference share race-free, the same
single-writer discipline as `Chunk`/`PaletteStorage` above: readers always see a consistent snapshot.
`LodColumnStore` (`Blocks/LodColumnStore.cs`) is a lock-based region dict with exactly one writer thread
(server: the LOD thread; client: the apply thread). **Do not add a second writer.**

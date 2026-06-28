using System;
using System.Threading;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Graphics.Rhi;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Client-side GPU mesh for a <see cref="Chunk"/>. The OPAQUE mesh is built into a CPU
    /// <see cref="MeshBuffer"/> (mesh thread) and uploaded into the shared <see cref="ChunkMeshArena"/> so the
    /// whole visible opaque set draws with one batched multidraw; the TRANSPARENT mesh keeps a per-chunk
    /// <see cref="SortedVertexArrayObject"/> (it needs an independent per-frame back-to-front sort). Chunk
    /// storage stays GPU-free so a headless server can build chunks without a GPU context.
    /// </summary>
    public class ChunkRenderData : IDisposable
    {
        /// <summary>The chunk currently meshed. Replaced when the server resends fresh chunk data.</summary>
        public Chunk Chunk;

        /// <summary>Fresh CPU mesh pending upload (set by the mesh thread, cleared by the main-thread upload).
        /// Gates the upload so a redundant upload doesn't blank the chunk (see TryUpload).</summary>
        public bool Updated;
        public bool Uploaded;

        /// <summary>Index into <see cref="MinecraftClone3API.Client.Blocks.WorldClient"/>'s main-thread
        /// render list; -1 when not listed. Enables O(1) swap-removal on eviction. Main-thread only.</summary>
        public int RenderListIndex = -1;

        /// <summary>True iff any cell in the chunk carries sky light: gates the sun shadow passes.
        /// Mesh-thread write / main-thread read; a benign torn read self-corrects on the next remesh.</summary>
        public bool SkyExposed = true;

        /// <summary>This chunk's opaque sub-range in the shared arena. IndexCount == 0 ⇒ no opaque geometry.
        /// Main-thread only (written by the upload loop, freed by the dispose drain).</summary>
        public ChunkMeshArena.Allocation OpaqueAlloc;

        /// <summary>World-space chunk centre. Constant for this render-data's lifetime (the Chunk is only ever
        /// replaced by another at the same position), so it's computed once instead of every access.</summary>
        public readonly Vector3 Middle;

        /// <summary>World-space minimum corner (the chunk's 16³ AABB origin), published to the arena's
        /// <see cref="ChunkMeta"/> for GPU frustum culling.</summary>
        public readonly Vector3 MinCorner;

        public bool HasOpaque => OpaqueAlloc.IndexCount > 0;
        public bool HasTransparency => _transparentVao.UploadedCount > 0;

        /// <summary>Total uploaded index count (opaque + transparent) — for the profiler's GPU upload volume.</summary>
        public int UploadedIndexCount => OpaqueAlloc.IndexCount + _transparentVao.UploadedCount;

        // Opaque CPU mesh (no GPU) uploaded into the arena; transparent keeps its own GPU vertex buffers.
        private readonly MeshBuffer _opaque = new MeshBuffer();
        private readonly SortedVertexArrayObject _transparentVao = new SortedVertexArrayObject();

        public ChunkRenderData(Chunk chunk)
        {
            Chunk = chunk;
            Middle = (chunk.Position * Chunk.Size + new Vector3i(Chunk.Size / 2)).ToVector3();
            MinCorner = (chunk.Position * Chunk.Size).ToVector3();
        }

        public void Update()
        {
            lock (_opaque)
            lock (_transparentVao)
            {
                //Re-mesh from scratch; the chunk may have changed since the last pass.
                _opaque.Clear();
                _transparentVao.Clear();
                AddBlocksToVao();
                Updated = true;
            }

            // Outside the locks (reads chunk storage only) so it doesn't lengthen the non-blocking-upload
            // contention window. Flags whether the chunk receives sky light, which gates the shadow passes.
            SkyExposed = Chunk.HasAnySkyLight();
        }

        /// <summary>
        /// Uploads the pending mesh — the opaque CPU buffer into the <paramref name="arena"/>, the transparent
        /// mesh into its own VAO — returning false <b>without blocking</b> when the mesh thread holds the
        /// buffers (a remesh is in progress). The caller retries next frame instead of stalling the render
        /// thread on the whole remesh (the per-edit frame spike this avoids; see CLAUDE.md).
        /// </summary>
        public bool TryUpload(ChunkMeshArena arena)
        {
            if (!Monitor.TryEnter(_opaque)) return false;
            try
            {
                if (!Monitor.TryEnter(_transparentVao)) return false;
                try
                {
                    // Only upload when a fresh mesh is pending; a redundant upload would otherwise re-allocate
                    // an empty range and blank the chunk until the next remesh.
                    if (!Updated) return true;

                    OpaqueAlloc = arena.Upload(OpaqueAlloc, _opaque, MinCorner);
                    _opaque.Clear();

                    _transparentVao.Upload();
                    _transparentVao.Clear();

                    Updated = false;
                }
                finally
                {
                    Monitor.Exit(_transparentVao);
                }
            }
            finally
            {
                Monitor.Exit(_opaque);
            }

            Uploaded = true;
            return true;
        }

        public void DrawTransparent(RenderPass pass) => _transparentVao.Draw(pass);
        public void SortTransparentFaces() => _transparentVao.Sort();

        /// <summary>Frees the arena sub-range. Main-thread only; call before <see cref="Dispose"/>.</summary>
        public void FreeArena(ChunkMeshArena arena)
        {
            arena.Free(OpaqueAlloc);
            OpaqueAlloc = default;
        }

        public void Dispose()
        {
            lock (_opaque)
            lock (_transparentVao)
            {
                _opaque.Clear();
                _transparentVao.Dispose();
            }
        }

        private void AddBlocksToVao()
        {
            // Chunks within the render distance always mesh at FULL per-block detail (no within-RD LOD — it
            // looked bad up close). The cheap LOD is the Phase-2 horizon, beyond the render distance only.
            var chunk = Chunk;
            var min = chunk.Min;
            var max = chunk.Max;
            for (var x = min.X; x <= max.X; x++)
            for (var y = min.Y; y <= max.Y; y++)
            for (var z = min.Z; z <= max.Z; z++)
            {
                var id = chunk.GetBlock(new Vector3i(x, y, z));
                if (id == 0) continue; //Remove GetBlock overhead of Air
                var block = GameRegistry.BlockRegistry[id];
                // Block-entities (chests) are drawn by the block-entity renderer as their own box model, not
                // baked into the chunk mesh.
                if (block.RendersAsBlockEntity) continue;
                ChunkMesher.AddBlockToVao(chunk.World, chunk.Position * Chunk.Size + new Vector3i(x, y, z), x, y, z,
                    block, _opaque, _transparentVao);
            }
        }
    }
}

using System;
using System.Threading;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Client-side GPU mesh for one Phase-2 LOD region (the distant horizon). Opaque-only — distant water/glass
    /// are dumped into the same opaque buffer with the water flag (no per-region transparent sort) — and meshed
    /// straight from the streamed <see cref="LodColumnStore"/> run-list, never from real blocks. Mirrors
    /// <see cref="ChunkRenderData"/>'s thread contract: <see cref="Update"/> is CPU-only (mesh thread, holds the
    /// buffer lock for the whole remesh), <see cref="TryUpload"/> is non-blocking (main-thread GL), the arena
    /// range + render-list index are main-thread only.
    /// </summary>
    public class LodRenderData : IDisposable
    {
        public readonly Vector3i RegionKey;

        /// <summary>World-space region bounding sphere (centre + radius) for the frustum/distance cull.</summary>
        public readonly Vector3 Middle;
        public readonly float Radius;

        public bool Updated;
        public bool Uploaded;

        /// <summary>Index into <see cref="MinecraftClone3API.Client.Blocks.WorldClient"/>'s main-thread LOD
        /// render list; -1 when not listed. O(1) swap-removal on eviction. Main-thread only.</summary>
        public int RenderListIndex = -1;

        public ChunkMeshArena.Allocation OpaqueAlloc;

        private readonly MeshBuffer _opaque = new MeshBuffer();

        public LodRenderData(Vector3i regionKey)
        {
            RegionKey = regionKey;
            // Region centre: 128-block XZ footprint, mid of the terrain band in Y. Radius generously covers the
            // footprint half-diagonal (~90) plus the surface→skirt vertical span (tops ~96, skirts to -40), so
            // the cull never drops a visible region (over-including a just-offscreen region is harmless).
            var cx = (regionKey.X << 7) + LodColumn.RegionBlocks / 2;
            var cz = (regionKey.Z << 7) + LodColumn.RegionBlocks / 2;
            Middle = new Vector3(cx, 24f, cz);
            Radius = 140f;
        }

        public void Update(LodColumnStore store)
        {
            lock (_opaque)
            {
                _opaque.Clear();
                ChunkMesher.AddLodColumnRegionToVao(store, RegionKey, _opaque);
                Updated = true;
            }
        }

        /// <summary>Non-blocking upload of the pending mesh into the LOD arena; false (retry next frame) when the
        /// mesh thread holds the buffer mid-remesh. Gated on <see cref="Updated"/> so a redundant upload is a
        /// no-op (it would otherwise blank the region). Main-thread GL.</summary>
        public bool TryUpload(ChunkMeshArena arena)
        {
            if (!Monitor.TryEnter(_opaque)) return false;
            try
            {
                if (!Updated) return true;
                OpaqueAlloc = arena.Upload(OpaqueAlloc, _opaque);
                _opaque.Clear();
                Updated = false;
            }
            finally
            {
                Monitor.Exit(_opaque);
            }

            Uploaded = true;
            return true;
        }

        public void FreeArena(ChunkMeshArena arena)
        {
            arena.Free(OpaqueAlloc);
            OpaqueAlloc = default;
        }

        public void Dispose()
        {
            lock (_opaque) _opaque.Clear();
        }
    }
}

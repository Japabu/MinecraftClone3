using System;
using System.Threading;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Client-side GPU mesh for a <see cref="Chunk"/>. Holds the vertex array objects and the
    /// meshing/upload/draw logic that used to live on <see cref="Chunk"/>, keeping chunk storage
    /// free of any GL types so a headless server can construct chunks without a GL context.
    /// </summary>
    public class ChunkRenderData : IDisposable
    {
        /// <summary>The chunk currently meshed. Replaced when the server resends fresh chunk data.</summary>
        public Chunk Chunk;

        public bool Updated;
        public bool Uploaded;

        /// <summary>Index into <see cref="MinecraftClone3API.Client.Blocks.WorldClient"/>'s main-thread
        /// render list (the renderer iterates that list instead of enumerating the RenderData
        /// ConcurrentDictionary each frame); -1 when not listed. Enables O(1) swap-removal on eviction.
        /// Main-thread only.</summary>
        public int RenderListIndex = -1;

        public Vector3 Middle => (Chunk.Position * Chunk.Size + new Vector3i(Chunk.Size / 2)).ToVector3();
        public bool HasTransparency => _transparentVao.UploadedCount > 0;

        /// <summary>Total uploaded index count (opaque + transparent) — surfaced so the profiler can
        /// report per-frame GPU upload volume.</summary>
        public int UploadedIndexCount => _vao.UploadedCount + _transparentVao.UploadedCount;

        private readonly VertexArrayObject _vao = new VertexArrayObject();
        private readonly SortedVertexArrayObject _transparentVao = new SortedVertexArrayObject();

        public ChunkRenderData(Chunk chunk)
        {
            Chunk = chunk;
        }

        public void Update()
        {
            lock (_vao)
            lock (_transparentVao)
            {
                //Re-mesh from scratch; the chunk may have changed since the last pass.
                _vao.Clear();
                _transparentVao.Clear();
                AddBlocksToVao();
                Updated = true;
            }
        }

        /// <summary>
        /// Uploads the pending mesh to the GPU, returning false <b>without blocking</b> when the mesh
        /// thread currently holds the VAO locks (a remesh is in progress). The caller retries next frame
        /// instead of stalling the render thread for the whole remesh: a single edit remeshes the chunk
        /// plus up to six face neighbours, and one remesh is tens of ms (per-vertex smooth-lighting
        /// neighbour sampling), so a <i>blocking</i> upload waiting on those locks was the per-edit
        /// frame-time spike. The render path (Draw/Sort) never takes these locks, so rendering itself is
        /// unaffected — only the upload handoff needed decoupling.
        /// </summary>
        public bool TryUpload()
        {
            if (!Monitor.TryEnter(_vao)) return false;
            try
            {
                if (!Monitor.TryEnter(_transparentVao)) return false;
                try
                {
                    // Only upload when a fresh mesh is pending. A redundant Upload would otherwise see
                    // the lists already consumed+cleared and zero UploadedCount, blanking the chunk until
                    // the next re-mesh.
                    if (!Updated) return true;

                    _vao.Upload();
                    _vao.Clear();

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
                Monitor.Exit(_vao);
            }

            Uploaded = true;
            return true;
        }

        public void Draw() => _vao.Draw();
        public void DrawTransparent() => _transparentVao.Draw();

        public void SortTransparentFaces() => _transparentVao.Sort();

        public void Dispose()
        {
            lock (_vao)
            lock (_transparentVao)
            {
                _vao.Dispose();
                _transparentVao.Dispose();
            }
        }

        private void AddBlocksToVao()
        {
            var chunk = Chunk;
            var min = chunk.Min;
            var max = chunk.Max;
            for (var x = min.X; x <= max.X; x++)
            for (var y = min.Y; y <= max.Y; y++)
            for (var z = min.Z; z <= max.Z; z++)
            {
                var id = chunk.GetBlock(new Vector3i(x, y, z));
                if (id != 0) //Remove GetBlock overhead of Air
                    ChunkMesher.AddBlockToVao(chunk.World, chunk.Position * Chunk.Size + new Vector3i(x, y, z), x, y, z,
                        GameRegistry.BlockRegistry[id], _vao, _transparentVao);
            }
        }
    }
}

using System;
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

        public Vector3 Middle => (Chunk.Position * Chunk.Size + new Vector3i(Chunk.Size / 2)).ToVector3();
        public bool HasTransparency => _transparentVao.UploadedCount > 0;

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

        public void Upload()
        {
            lock (_vao)
            lock (_transparentVao)
            {
                // Only upload when a fresh mesh is pending. A redundant Upload would otherwise see
                // the lists already consumed+cleared and zero UploadedCount, blanking the chunk until
                // the next re-mesh.
                if (!Updated) return;

                _vao.Upload();
                _vao.Clear();

                _transparentVao.Upload();
                _transparentVao.Clear();

                Updated = false;
            }
            Uploaded = true;
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

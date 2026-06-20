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

        /// <summary>All 15 face-pair bits set: fully see-through (a chunk a sight-line passes through freely).</summary>
        public const int AllConnected = (1 << 15) - 1;

        /// <summary>Per-chunk visibility connectivity for occlusion culling: 15 unordered-face-pair bits
        /// (faces 0=-X 1=+X 2=-Y 3=+Y 4=-Z 5=+Z, opposite = f^1) — bit set iff a sight-line can pass through
        /// this chunk between those two faces via non-opaque cells. The render-time BFS from the camera chunk
        /// uses it to stop traversal at solid chunks. Written on the mesh thread in <see cref="Update"/>, read
        /// on the main thread by the renderer; a benign torn read self-corrects on the next remesh (same race
        /// rule as <see cref="MinecraftClone3API.Blocks.PaletteStorage"/>). AllConnected until first meshed so
        /// a not-yet-meshed chunk never seals the player in.</summary>
        public int Connectivity = AllConnected;

        /// <summary>True iff any cell in the chunk carries sky light (<see cref="Chunk.HasAnySkyLight"/>):
        /// gates the sun shadow passes, which are skipped when no visible chunk is sky-exposed (deep cave).
        /// Mesh-thread write / main-thread read, same torn-read rule as <see cref="Connectivity"/>.</summary>
        public bool SkyExposed = true;

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

            // Outside the VAO locks (reads chunk storage only, no GL/VAO state): the visibility graph and
            // sky-exposure flag the render-time BFS reads. Keeping it out of the locks avoids lengthening
            // the window the non-blocking TryUpload contends with.
            Connectivity = ComputeConnectivity(Chunk);
            SkyExposed = Chunk.HasAnySkyLight();
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

        // Maps an unordered face pair to its connectivity bit (symmetric, built once). ComputeConnectivity
        // (producer) and WorldRenderer's BFS (consumer) both go through PairBit, so the bit layout is defined
        // in exactly one place.
        private static readonly int[] _pairBit = BuildPairBits();

        private static int[] BuildPairBits()
        {
            var table = new int[36];
            var idx = 0;
            for (var a = 0; a < 6; a++)
            for (var b = a + 1; b < 6; b++)
                table[a * 6 + b] = table[b * 6 + a] = 1 << idx++;
            return table;
        }

        public static int PairBit(int faceA, int faceB) => _pairBit[faceA * 6 + faceB];

        // Reused mesh-thread-only scratch for the connectivity flood (Update is called only on the single
        // mesh thread), so a remesh allocates nothing here.
        private static readonly byte[] _connSeeThrough = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        private static readonly int[] _connComp = new int[Chunk.Size * Chunk.Size * Chunk.Size];
        private static readonly int[] _connQueue = new int[Chunk.Size * Chunk.Size * Chunk.Size];

        /// <summary>Mesh-thread connected-component flood over the chunk's see-through cells (air or any
        /// non-opaque-full block — glass/torches/slabs pass), producing the <see cref="Connectivity"/>
        /// face-pair bitset: two faces connect iff one see-through component touches both. Fast paths: an
        /// empty (all-air) chunk is fully connected; a chunk with no see-through cell (solid rock) connects
        /// nothing — together these cover almost every chunk in the thin-heightmap world.</summary>
        private static int ComputeConnectivity(Chunk chunk)
        {
            if (chunk.IsEmpty) return AllConnected;

            const int size = Chunk.Size;
            const int area = size * size;
            var world = chunk.World;
            var basePos = chunk.Position * size;
            var seeThrough = _connSeeThrough;
            var comp = _connComp;
            var queue = _connQueue;

            var anySeeThrough = false;
            for (var x = 0; x < size; x++)
            for (var y = 0; y < size; y++)
            for (var z = 0; z < size; z++)
            {
                var index = Chunk.Index(x, y, z);
                var id = chunk.GetBlock(new Vector3i(x, y, z));
                var through = id == 0 ||
                              !GameRegistry.BlockRegistry[id].IsOpaqueFullBlock(world, basePos + new Vector3i(x, y, z));
                seeThrough[index] = (byte) (through ? 1 : 0);
                comp[index] = 0;
                if (through) anySeeThrough = true;
            }

            if (!anySeeThrough) return 0;

            var connectivity = 0;
            for (var start = 0; start < seeThrough.Length; start++)
            {
                if (seeThrough[start] == 0 || comp[start] != 0) continue;

                var faceMask = 0;
                var head = 0;
                var tail = 0;
                queue[tail++] = start;
                comp[start] = 1;

                while (head < tail)
                {
                    var cur = queue[head++];
                    var z = cur % size;
                    var y = cur / size % size;
                    var x = cur / area;

                    if (x == 0) faceMask |= 1 << 0;
                    if (x == size - 1) faceMask |= 1 << 1;
                    if (y == 0) faceMask |= 1 << 2;
                    if (y == size - 1) faceMask |= 1 << 3;
                    if (z == 0) faceMask |= 1 << 4;
                    if (z == size - 1) faceMask |= 1 << 5;

                    if (x > 0 && seeThrough[cur - area] == 1 && comp[cur - area] == 0) { comp[cur - area] = 1; queue[tail++] = cur - area; }
                    if (x < size - 1 && seeThrough[cur + area] == 1 && comp[cur + area] == 0) { comp[cur + area] = 1; queue[tail++] = cur + area; }
                    if (y > 0 && seeThrough[cur - size] == 1 && comp[cur - size] == 0) { comp[cur - size] = 1; queue[tail++] = cur - size; }
                    if (y < size - 1 && seeThrough[cur + size] == 1 && comp[cur + size] == 0) { comp[cur + size] = 1; queue[tail++] = cur + size; }
                    if (z > 0 && seeThrough[cur - 1] == 1 && comp[cur - 1] == 0) { comp[cur - 1] = 1; queue[tail++] = cur - 1; }
                    if (z < size - 1 && seeThrough[cur + 1] == 1 && comp[cur + 1] == 0) { comp[cur + 1] = 1; queue[tail++] = cur + 1; }
                }

                for (var a = 0; a < 6; a++)
                {
                    if ((faceMask & (1 << a)) == 0) continue;
                    for (var b = a + 1; b < 6; b++)
                        if ((faceMask & (1 << b)) != 0)
                            connectivity |= PairBit(a, b);
                }
            }

            return connectivity;
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

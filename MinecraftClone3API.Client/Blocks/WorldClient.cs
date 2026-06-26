using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Networking;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Client.Blocks
{
    /// <summary>
    /// Client-side replica of the world. Chunk decode (the singleplayer clone / the multiplayer
    /// decompress + deserialize) and all chunk-content mutation run on a background <b>apply thread</b>,
    /// so the render thread never copies chunk storage. The main thread only pumps packets, does the GL
    /// work (creating <see cref="ChunkRenderData"/>, uploading meshes) and reads; a separate mesh thread
    /// builds vertex lists. It never generates terrain, touches disk, or runs lighting — the server is
    /// authoritative.
    /// </summary>
    public class WorldClient : WorldBase
    {
        // Per-frame GL upload time budget. Uploads are cheap (~0.15 ms each — a glBufferData/SubData orphan),
        // so a few ms drains tens of chunks per frame, comfortably above the mesh pool's output, while bounding
        // the upload's contribution to frame time. Replaces a fixed count-per-frame (8) that throttled the
        // world-fill below the pool's throughput (an F10 chunk-trace showed the upload queue ballooning to
        // ~900 and chunks waiting seconds in it); a time budget self-scales to how cheap uploads actually are.
        private const double UploadBudgetMs = 4.0;
        private static readonly long UploadBudgetTicks = (long) (UploadBudgetMs * Stopwatch.Frequency / 1000.0);

        // Cap on packets handled per frame. Well above the steady-state inflow (the server streams at
        // most ServerNetwork.MaxChunksPerTick chunks per tick plus deltas), so it never throttles normal
        // play but bounds a pathological burst to one frame's worth instead of stalling the render thread.
        private const int MaxPacketsPerTick = 64;

        // Cap on chunks given GL render data (and queued for meshing) per frame. The apply thread can
        // publish a burst; bounding the per-frame GL/VAO creation keeps a burst from stalling the frame.
        private const int MaxRenderReadyPerTick = 256;

        // Distance (blocks, chunk-centre to player) past which the client drops a cached chunk and tells the
        // server with a ChunkRelease. Must stay comfortably above the server's send range (so a chunk the
        // server just streamed is never evicted-then-re-requested at the boundary — the gap is the caching
        // hysteresis that makes walking away and back cost zero bytes). In SP it tracks the render-distance
        // slider (StateWorld sets it = render distance + a hysteresis gap); in MP it stays at the safe default
        // (the client can't know the remote server's view distance, so it keeps caching what's streamed).
        public const float CacheHysteresis = 80f;
        private float _cacheDistanceSq = 240f * 240f;

        public float CacheDistance
        {
            get => MathF.Sqrt(_cacheDistanceSq);
            set
            {
                _cacheDistanceSq = value * value;
                // Force the next eviction scan (normally gated on a chunk-border crossing) so a *decrease*
                // evicts the now-too-far chunks immediately instead of waiting for the player to move.
                _lastEvictChunk = new Vector3i(int.MinValue);
            }
        }

        private static readonly Vector3i[] NeighbourOffsets =
        {
            new Vector3i(-1, 0, 0), new Vector3i(+1, 0, 0),
            new Vector3i(0, -1, 0), new Vector3i(0, +1, 0),
            new Vector3i(0, 0, -1), new Vector3i(0, 0, +1)
        };

        public readonly ConcurrentDictionary<Vector3i, ChunkRenderData> RenderData =
            new ConcurrentDictionary<Vector3i, ChunkRenderData>();

        // Shared vertex/index arena holding every chunk's OPAQUE mesh, drawn with one batched multidraw per
        // pass (see ChunkMeshArena). Created/uploaded/freed/disposed on the main thread only.
        public readonly ChunkMeshArena OpaqueArena;

        // Main-thread mirror of RenderData's values, iterated by the renderer each frame in place of
        // enumerating the ConcurrentDictionary (a per-frame O(bucket) scan + enumerator allocation a
        // trace showed dominating the render thread). Kept in sync O(1) on add (DrainRenderReady) and
        // swap-removal on evict (UnloadChunk) via ChunkRenderData.RenderListIndex. Only the main thread
        // touches this list — the mesh thread looks chunks up through RenderData directly.
        public readonly List<ChunkRenderData> RenderList = new List<ChunkRenderData>();

        public readonly Dictionary<int, Entity> Entities = new Dictionary<int, Entity>();

        /// <summary>Client replica of the server-authoritative inventory. Synced on join via
        /// <see cref="InventoryStatePacket"/>; the GUI and hotbar mutate it optimistically and report changes
        /// to the server with <see cref="SendInventoryAction"/> / <see cref="SendHeldSlot"/>.</summary>
        public readonly Inventory Inventory = new Inventory();

        /// <summary>Local replicas of open container blocks (furnaces), keyed by block position. Updated from
        /// <see cref="ContainerStatePacket"/>s and read by the container screens (see <see cref="ContainerView"/>).</summary>
        public readonly Dictionary<Vector3i, ContainerView> Containers = new Dictionary<Vector3i, ContainerView>();

        public int LocalEntityId = -1;

        // Local mirror of the server-authoritative survival stats (from PlayerStatsPacket), read by the
        // survival HUD, the Flying gate, and the death screen. StatsReceived gates the HUD until the first sync.
        public float Health;
        public float MaxHealth = 20f;
        public float Hunger;
        public float Saturation;
        public GameMode GameMode = GameMode.Creative;
        public bool PlayerDead;
        public bool StatsReceived;

        public Vector3 SpawnPosition;
        public bool SpawnReceived;
        public bool Ready;

        // Server-authoritative world clock: the last received world time plus the wall-clock since it
        // arrived, so the day/night cycle advances smoothly between the periodic WorldTime packets and is
        // shared across multiplayer clients (WorldRenderer reads this).
        private double _serverTimeSeconds;
        private readonly Stopwatch _timeSyncClock = Stopwatch.StartNew();
        public double WorldTimeSeconds => _serverTimeSeconds + _timeSyncClock.Elapsed.TotalSeconds;

        // Per-Update phase timings + GL upload volume, surfaced to the profiler so a frame spike can be
        // split into packet-handling / render-data creation / GL upload / eviction — isolating whether the
        // cost is the update path or the GPU upload (the re-BufferData of edited chunks).
        public double LastPacketMs, LastDrainMs, LastUploadMs, LastEvictMs;
        public int LastUploadChunks, LastUploadIndices;

        // Lock-free mirrors of the locked queue/dictionary sizes so the profiler (F3 reads these every
        // frame, including while destroying) does not take _meshLock or ConcurrentDictionary.Count (an
        // all-stripe lock) and contend with the apply/mesh threads — that contention is what made enabling
        // F3 worsen the very stutter it was measuring.
        private volatile int _meshQueueDepth;
        private volatile int _uploadQueueDepth;
        private int _loadedChunkCount;
        public int MeshQueueDepth => _meshQueueDepth;
        public int UploadQueueDepth => _uploadQueueDepth;
        public int LoadedChunkCount => _loadedChunkCount;

        // The concurrent-queue backlogs upstream of meshing, mirrored as Interlocked-maintained counters
        // (incremented at the producing enqueue, decremented at the consuming dequeue) so the profiler
        // reads them lock-free instead of calling ConcurrentQueue.Count each frame. These are the decode
        // backlog, the decoded→GL backlog, and the GL-dispose backlog respectively.
        private int _applyQueueDepth;
        private int _renderReadyDepth;
        private int _disposeQueueDepth;
        public int ApplyQueueDepth => _applyQueueDepth;
        public int RenderReadyQueueDepth => _renderReadyDepth;
        public int DisposeQueueDepth => _disposeQueueDepth;

        private readonly Stopwatch _phaseTimer = new Stopwatch();
        private readonly Stopwatch _entityInterpTimer = Stopwatch.StartNew();

        private readonly IConnection _connection;
        private readonly Thread[] _meshThreads;

        // Decodes streamed chunks and applies block-change deltas off the render thread, in packet order
        // (the single writer of chunk contents). It publishes finished chunks to LoadedChunks and hands
        // their positions to the main thread via _renderReady for the GL-only ChunkRenderData step.
        private readonly Thread _applyThread;
        private readonly ConcurrentQueue<Packet> _applyQueue = new ConcurrentQueue<Packet>();
        private readonly AutoResetEvent _applySignal = new AutoResetEvent(false);
        private readonly ConcurrentQueue<(Vector3i Position, bool HighPriority)> _renderReady =
            new ConcurrentQueue<(Vector3i, bool)>();

        // Phase-2 LOD horizon (mirrors the chunk pipeline). The apply thread is the SOLE client writer of
        // LodStore (decoding LodColumnData on the same ordered _applyQueue); decoded regions are handed to the
        // main thread via _lodRenderReady for GL render-data creation + meshing (Stage 7).
        public readonly LodColumnStore LodStore = new LodColumnStore();
        private readonly ConcurrentQueue<Vector3i> _lodRenderReady = new ConcurrentQueue<Vector3i>();
        private int _lodRenderReadyDepth;
        public int LodRenderReadyQueueDepth => _lodRenderReadyDepth;

        // Phase-2 LOD render side (main-thread owned, like RenderData/RenderList). LodRegions is a by-key
        // lookup; LodRenderList mirrors its values for the renderer to iterate. Meshing + uploading reuse the
        // chunk mesh pool + upload loop as a lowest-priority branch (separate queues under the same locks).
        public ChunkMeshArena LodArena;
        public readonly ConcurrentDictionary<Vector3i, LodRenderData> LodRegions =
            new ConcurrentDictionary<Vector3i, LodRenderData>();
        public readonly List<LodRenderData> LodRenderList = new List<LodRenderData>();
        private readonly Queue<Vector3i> _lodMeshQueue = new Queue<Vector3i>();
        private readonly HashSet<Vector3i> _lodMeshPending = new HashSet<Vector3i>();
        private readonly Queue<Vector3i> _lodUploadQueue = new Queue<Vector3i>();
        private readonly HashSet<Vector3i> _lodUploadPending = new HashSet<Vector3i>();
        private readonly List<Vector3i> _lodUploadRequeueScratch = new List<Vector3i>(64);
        private readonly ConcurrentQueue<LodRenderData> _lodDisposeQueue = new ConcurrentQueue<LodRenderData>();
        private readonly List<Vector3i> _lodEvictScratch = new List<Vector3i>();
        private Vector3i _lastLodEvictChunk = new Vector3i(int.MinValue);

        // Block radius for the LOD draw cull + cache eviction. Set by StateWorld from the render-distance config
        // (= server LodRadius in blocks); 0 by default ⇒ LOD dormant. CacheDistance kept a region past the draw
        // distance so a region isn't evicted-then-re-streamed at the boundary.
        public float LodRenderDistance;
        private float _lodCacheDistanceSq;
        public float LodCacheDistance { set => _lodCacheDistanceSq = value * value; }
        public int LodRegionCount => LodRenderList.Count;

        private readonly object _meshLock = new object();
        // High priority = edits/light resends (re-applies of already-rendered chunks) so interactive
        // changes mesh promptly instead of waiting behind the initial first-load chunk flood.
        private readonly Queue<Vector3i> _meshQueueHigh = new Queue<Vector3i>();
        private readonly Queue<Vector3i> _meshQueueLow = new Queue<Vector3i>();
        private readonly HashSet<Vector3i> _meshPending = new HashSet<Vector3i>();

        private readonly object _uploadLock = new object();
        private readonly Queue<Vector3i> _uploadQueue = new Queue<Vector3i>();
        private readonly HashSet<Vector3i> _uploadPending = new HashSet<Vector3i>();

        private readonly ConcurrentQueue<ChunkRenderData> _disposeQueue = new ConcurrentQueue<ChunkRenderData>();

        // Last position reported via SendMove; drives client-owned chunk eviction. Reused scratch list
        // collects the chunks to evict so the per-frame scan allocates nothing steady-state.
        private Vector3 _playerPosition;
        // Player chunk at the last eviction scan; eviction only matters when the player moves, since chunks
        // only ever stream in within ViewDistance (< CacheDistance), so nothing goes out of cache range while
        // stationary. Gating on chunk-border crossings skips the per-frame O(loaded) scan when standing still.
        private Vector3i _lastEvictChunk = new Vector3i(int.MinValue);
        private readonly List<Vector3i> _evictScratch = new List<Vector3i>();
        private readonly List<Vector3i> _uploadRequeueScratch = new List<Vector3i>(64);

        private volatile bool _stopped;

        /// <summary>Generic per-dimension visuals the renderer reads (no engine knowledge of specific
        /// dimensions): <see cref="HasSky"/> false drops the sun/day-night and stars and uses <see cref="FogColor"/>
        /// for the sky/fog, and <see cref="AmbientLight"/> is a minimum light floor. Set by a
        /// <see cref="DimensionChangePacket"/>; defaults to the open-sky Overworld.</summary>
        public bool HasSky = true;
        public Vector3 FogColor;
        public Vector3 AmbientLight;

        // Dimension-change barrier. _resetting parks the apply thread (sole writer of LoadedChunks/LodStore) so
        // the main thread can drop the whole cached world race-free in ResetForDimensionChange; _applyParked is
        // the apply thread's acknowledgement. _dimensionChanged is the one-shot the host (StateWorld) consumes to
        // re-enter its loading screen for the new dimension.
        private volatile bool _resetting;
        private volatile bool _applyParked;
        private volatile bool _dimensionChanged;

        public WorldClient(IConnection connection)
        {
            _connection = connection;

            // GL: constructed on the main thread (StateWorld ctor runs in the state update with a live context).
            OpaqueArena = new ChunkMeshArena();
            LodArena = new ChunkMeshArena();

            _applyThread = new Thread(ApplyThread) {Name = "Client Apply Thread", IsBackground = true};
            _applyThread.Start();

            // Meshing is the chunk-pipeline bottleneck (an F10 trace showed chunks spending ~99% of their
            // load latency waiting in the mesh queue, the single mesh thread pegged at 100%). It is
            // embarrassingly parallel: workers mesh distinct chunks (the _meshPending claim under _meshLock
            // guarantees no two take the same one), read chunk storage without writing (copy-on-grow
            // tolerates concurrent readers — Invariant 5), and only hand finished meshes to the main-thread
            // GL upload (Invariant 1), so a pool scales throughput ~linearly with cores. Leave two cores for
            // the main (render) and server load threads.
            var meshWorkers = Math.Max(1, Environment.ProcessorCount - 2);
            // Reserve a fraction of the pool as LOD-first workers so the distant horizon can never be fully
            // starved by chunk meshing. The chunk-first workers still treat LOD as lowest priority (near terrain
            // stays responsive), but with sustained movement the detail-chunk queue is *never* empty, so without
            // a reserved worker the LOD mesh queue grows unboundedly and the whole horizon goes unmeshed (the
            // "walk far → every LOD super low-res" bug). Both kinds fall through to the other queue when their
            // own is empty, so no worker idles while any mesh work remains. Single-core pools keep one
            // chunk-first worker (can't dedicate the only one).
            var lodWorkers = Math.Min(Math.Max(1, meshWorkers / 4), meshWorkers - 1);
            _meshThreads = new Thread[meshWorkers];
            for (var i = 0; i < meshWorkers; i++)
            {
                var lodFirst = i < lodWorkers;
                _meshThreads[i] = new Thread(() => MeshThread(lodFirst)) {Name = $"Client Mesh Thread {i}", IsBackground = true};
                _meshThreads[i].Start();
            }
        }

        public override void Update()
        {
            _phaseTimer.Restart();
            var packets = 0;
            while (packets < MaxPacketsPerTick && _connection.TryReceive(out var packet))
            {
                HandlePacket(packet);
                packets++;
            }
            LastPacketMs = _phaseTimer.Elapsed.TotalMilliseconds;

            _phaseTimer.Restart();
            DrainRenderReady();
            DrainLodRenderReady();
            LastDrainMs = _phaseTimer.Elapsed.TotalMilliseconds;

            _phaseTimer.Restart();
            // Upload meshed chunks until a per-frame time budget is spent, then leave the rest queued. The
            // mesh pool produces chunks far faster than one frame can upload, so a fixed count-per-frame
            // throttled the fill; the budget drains as many as fit in a few ms (uploads are cheap) and bounds
            // the frame cost. Each queued position is dequeued at most once per frame.
            var uploadDeadline = Stopwatch.GetTimestamp() + UploadBudgetTicks;
            var requeue = _uploadRequeueScratch;
            requeue.Clear();
            var uploadChunks = 0;
            var uploadIndices = 0;
            while (true)
            {
                Vector3i pos;
                lock (_uploadLock)
                {
                    if (_uploadQueue.Count == 0)
                    {
                        _uploadQueueDepth = 0;
                        break;
                    }

                    pos = _uploadQueue.Dequeue();
                    _uploadPending.Remove(pos);
                    _uploadQueueDepth = _uploadQueue.Count;
                }

                // TryUpload never blocks: if the mesh thread is mid-remesh of this chunk (holding its VAO
                // locks) it returns false; collect it and requeue *after* the loop so it retries a later
                // frame (never re-dequeued this frame) instead of the render thread stalling on the remesh
                // lock. That blocking wait was the per-edit frame-time spike. See ChunkRenderData.TryUpload.
                if (RenderData.TryGetValue(pos, out var renderData))
                {
                    if (renderData.TryUpload(OpaqueArena))
                    {
                        ChunkTracer.Uploaded(pos);
                        uploadChunks++;
                        uploadIndices += renderData.UploadedIndexCount;
                    }
                    else
                    {
                        requeue.Add(pos);
                    }
                }

                if (Stopwatch.GetTimestamp() >= uploadDeadline) break;
            }

            foreach (var pos in requeue) RequeueUpload(pos);

            // LOD region uploads share the same per-frame budget, drained AFTER chunks (lowest priority) so the
            // distant horizon never delays detail-chunk uploads. Whatever budget remains this frame drains LOD.
            var lodRequeue = _lodUploadRequeueScratch;
            lodRequeue.Clear();
            while (Stopwatch.GetTimestamp() < uploadDeadline)
            {
                Vector3i key;
                lock (_uploadLock)
                {
                    if (_lodUploadQueue.Count == 0) break;
                    key = _lodUploadQueue.Dequeue();
                    _lodUploadPending.Remove(key);
                }
                if (LodRegions.TryGetValue(key, out var lodData))
                {
                    if (lodData.TryUpload(LodArena))
                    {
                        uploadChunks++;
                        uploadIndices += lodData.OpaqueAlloc.IndexCount;
                    }
                    else lodRequeue.Add(key);
                }
            }
            foreach (var key in lodRequeue) RequeueLodUpload(key);

            LastUploadChunks = uploadChunks;
            LastUploadIndices = uploadIndices;
            LastUploadMs = _phaseTimer.Elapsed.TotalMilliseconds;

            _phaseTimer.Restart();
            EvictDistantChunks();
            EvictDistantLod();
            ScanLodForMeshStep();
            LastEvictMs = _phaseTimer.Elapsed.TotalMilliseconds;

            while (_disposeQueue.TryDequeue(out var disposed))
            {
                Interlocked.Decrement(ref _disposeQueueDepth);
                disposed.FreeArena(OpaqueArena);
                disposed.Dispose();
            }

            while (_lodDisposeQueue.TryDequeue(out var lodDisposed))
            {
                lodDisposed.FreeArena(LodArena);
                lodDisposed.Dispose();
            }

            // Advance remote-entity interpolation at the display rate (positions arrive at 20 tps).
            var interpDt = (float) _entityInterpTimer.Elapsed.TotalSeconds;
            _entityInterpTimer.Restart();
            foreach (var entity in Entities.Values) entity.UpdateInterpolation(interpDt);
        }

        /// <summary>Creates the GL render data (a GL call, hence main-thread only) for chunks the apply
        /// thread finished decoding/mutating, and queues them for meshing. The apply thread already
        /// published the chunk to <see cref="WorldBase.LoadedChunks"/>; this only wires up rendering.</summary>
        private void DrainRenderReady()
        {
            var processed = 0;
            while (processed < MaxRenderReadyPerTick && _renderReady.TryDequeue(out var ready))
            {
                processed++;
                Interlocked.Decrement(ref _renderReadyDepth);

                if (!LoadedChunks.TryGetValue(ready.Position, out var chunk)) continue;

                if (!RenderData.TryGetValue(ready.Position, out var renderData))
                {
                    RenderData[ready.Position] = renderData = new ChunkRenderData(chunk);
                    renderData.RenderListIndex = RenderList.Count;
                    RenderList.Add(renderData);
                }

                renderData.Chunk = chunk;
                QueueMesh(ready.Position, ready.HighPriority);
            }
        }

        /// <summary>Main thread: creates LOD render-data (GL-free CPU buffer; arena alloc happens at upload) for
        /// regions the apply thread decoded, and queues them for meshing on the shared pool. Mirrors
        /// <see cref="DrainRenderReady"/>.</summary>
        private void DrainLodRenderReady()
        {
            var processed = 0;
            while (processed < MaxRenderReadyPerTick && _lodRenderReady.TryDequeue(out var key))
            {
                processed++;
                Interlocked.Decrement(ref _lodRenderReadyDepth);

                if (!LodRegions.TryGetValue(key, out var lodData))
                {
                    LodRegions[key] = lodData = new LodRenderData(key);
                    lodData.DesiredMeshStep = MeshStepFor(lodData.Middle);
                    lodData.RenderListIndex = LodRenderList.Count;
                    LodRenderList.Add(lodData);
                }

                QueueLodMesh(key);
            }
        }

        private void QueueLodMesh(Vector3i key)
        {
            lock (_meshLock)
                if (_lodMeshPending.Add(key))
                    _lodMeshQueue.Enqueue(key);
        }

        // DH power-of-two detail rings, RELATIVE to the render distance, scaled by LOD Quality. With the stride-2
        // LOD store, meshStep 1/2/4/8 = stride 2/4/8/16. The NEAREST ring is stride-2 (the store's finest), so the
        // horizon just past the render distance is fine (a gentle step down from full detail) and only coarsens
        // farther out (fog-hidden) to stay cheap. Rings are >= 1 region (8 chunks) wide so adjacent regions never
        // differ by more than one step (no >2x crack).
        private const float LodRing1Distance = 16 * Chunk.Size;   // stride-2 within this many blocks past RD
        private const float LodRing2Distance = 32 * Chunk.Size;   // then stride-4
        private const float LodRing3Distance = 64 * Chunk.Size;   // then stride-8; beyond: stride-16
        private Vector3i _lastLodStepChunk = new Vector3i(int.MinValue);

        private int MeshStepFor(Vector3 middle)
        {
            // Horizontal (XZ) distance: the LOD regions form a horizontal annulus and the rings are defined in the
            // ground plane (EvictDistantLod uses XZ too), so a region's stride must not change with the player's
            // altitude — a full 3D distance would spuriously coarsen the whole horizon when flying high.
            var dx = middle.X - _playerPosition.X;
            var dz = middle.Z - _playerPosition.Z;
            var d = MathF.Sqrt(dx * dx + dz * dz) - GraphicsSettings.RenderDistanceChunks * Chunk.Size;
            // LOD Quality pushes the rings out (finer horizon farther) or pulls them in (coarser, faster).
            var q = GraphicsSettings.LodHorizonQuality;
            if (d < LodRing1Distance * q) return 1;   // stride-2
            if (d < LodRing2Distance * q) return 2;   // stride-4
            if (d < LodRing3Distance * q) return 4;   // stride-8
            return 8;                                  // stride-16
        }

        /// <summary>Forces the next <see cref="ScanLodForMeshStep"/> to re-evaluate every LOD region's stride
        /// (after a LOD-Quality change, which shifts the ring distances). Main-thread only.</summary>
        public void ForceLodMeshRescan() => _lastLodStepChunk = new Vector3i(int.MinValue);

        /// <summary>Re-evaluates each LOD region's mesh stride against the player's new position and re-queues any
        /// whose ring changed. Gated on a chunk-border crossing (own gate), so a stationary player does no work.
        /// Main-thread only (touches LodRenderList).</summary>
        private void ScanLodForMeshStep()
        {
            var playerChunk = ChunkInWorld(_playerPosition.ToVector3i());
            if (playerChunk == _lastLodStepChunk) return;
            _lastLodStepChunk = playerChunk;

            for (var i = 0; i < LodRenderList.Count; i++)
            {
                var rd = LodRenderList[i];
                var want = MeshStepFor(rd.Middle);
                if (want == rd.DesiredMeshStep) continue;
                rd.DesiredMeshStep = want;
                QueueLodMesh(rd.RegionKey);
            }
        }

        private void RequeueLodUpload(Vector3i key)
        {
            lock (_uploadLock)
                if (_lodUploadPending.Add(key))
                    _lodUploadQueue.Enqueue(key);
        }

        /// <summary>Debug/inspection toggle: when true the Phase-2 distant horizon is not drawn (so the
        /// inspection tool can A/B the horizon against full-detail-only). There is no within-RD LOD to toggle —
        /// chunks inside the render distance always mesh at full per-block detail.</summary>
        public volatile bool ForceLodOff;

        /// <summary>Re-queues every rendered chunk for re-meshing (used by the inspection tool). Main-thread only
        /// (touches RenderList).</summary>
        public void RemeshAll()
        {
            for (var i = 0; i < RenderList.Count; i++)
                QueueMesh(RenderList[i].Chunk.Position, false);
        }

        /// <summary>Drops chunks the player has moved away from and tells the server it released them.
        /// The client owns chunk lifetime; the server only streams and resends-on-change.</summary>
        private void EvictDistantChunks()
        {
            var playerChunk = ChunkInWorld(_playerPosition.ToVector3i());
            if (playerChunk == _lastEvictChunk) return;
            _lastEvictChunk = playerChunk;

            _evictScratch.Clear();
            foreach (var entry in LoadedChunks)
            {
                var center = (entry.Key * Chunk.Size + new Vector3i(Chunk.Size / 2)).ToVector3();
                if ((center - _playerPosition).LengthSquared > _cacheDistanceSq)
                    _evictScratch.Add(entry.Key);
            }

            foreach (var pos in _evictScratch)
            {
                UnloadChunk(pos);
                _connection.Send(new ChunkReleasePacket {Position = pos});
            }
        }

        /// <summary>Sends the local player's position so the server streams chunks around it and
        /// relays the movement to other clients.</summary>
        public void SendMove(EntityPlayer player)
        {
            _playerPosition = player.Position;
            _connection.Send(new EntityMovePacket
            {
                EntityId = LocalEntityId,
                Position = player.Position,
                Pitch = player.Pitch,
                Yaw = player.Yaw
            });
        }

        public void Login() => _connection.Send(new LoginPacket());

        /// <summary>Reports a single slot's new contents to the server (creative set-slot). The caller has
        /// already updated the local replica; this keeps the authoritative copy in step for persistence.</summary>
        public void SendInventoryAction(int slotIndex, ItemStack stack)
            => _connection.Send(new InventoryActionPacket {SlotIndex = slotIndex, Stack = stack});

        public void SendHeldSlot(int selectedHotbar)
            => _connection.Send(new HeldSlotPacket {SelectedHotbar = selectedHotbar});

        /// <summary>Tells the server this client opened the container block at <paramref name="pos"/> (a furnace),
        /// so it streams that block's state; the returned <see cref="ContainerView"/> is the screen's live replica.</summary>
        public ContainerView OpenContainer(Vector3i pos, int slotCount, int fieldCount)
        {
            if (!Containers.TryGetValue(pos, out var view) || view.Slots.Length != slotCount)
            {
                view = new ContainerView(slotCount, fieldCount);
                Containers[pos] = view;
            }
            _connection.Send(new OpenContainerPacket {Position = pos});
            return view;
        }

        public void CloseContainer(Vector3i pos)
        {
            Containers.Remove(pos);
            _connection.Send(new CloseContainerPacket());
        }

        public void SendContainerSlot(Vector3i pos, int slot, ItemStack stack)
            => _connection.Send(new ContainerSlotPacket {Position = pos, Slot = slot, Stack = stack});

        public void SendUseItem(Vector3i position)
            => _connection.Send(new UseItemRequestPacket {Position = position});

        public void SendUseItemOnEntity(int entityId)
            => _connection.Send(new UseItemOnEntityRequestPacket {EntityId = entityId});

        public void SendAttackEntity(int entityId)
            => _connection.Send(new AttackEntityRequestPacket {EntityId = entityId});

        /// <summary>Reports a completed fall (distance in blocks) so the server can apply fall damage.</summary>
        public void SendFall(float distance)
            => _connection.Send(new PlayerFallPacket {FallDistance = distance});

        /// <summary>Asks the server to switch game mode (the pause-menu toggle). Updates the local mirror
        /// optimistically so the menu/HUD/flight respond instantly even while a singleplayer pause has frozen
        /// the server pump; the server confirms the same value idempotently on unpause.</summary>
        public void SendSetGameMode(GameMode mode)
        {
            GameMode = mode;
            StatsReceived = true;
            _connection.Send(new SetGameModeRequestPacket {GameMode = (byte) mode});
        }

        /// <summary>Asks the server to respawn the dead player (the death-screen button).</summary>
        public void SendRespawn()
            => _connection.Send(new RespawnRequestPacket());

        /// <summary>Asks the server to drop the held hotbar item (one, or the whole stack when
        /// <paramref name="all"/>). The server is authoritative for the inventory and echoes the result back.</summary>
        public void SendDropItem(bool all)
            => _connection.Send(new DropItemRequestPacket {All = all});

        public void Disconnect()
        {
            _stopped = true;
            _applySignal.Set();
            _connection.Close();
            // Main-thread GL teardown of the shared arenas (the mesh/apply threads no longer touch them).
            OpaqueArena.Dispose();
            LodArena.Dispose();
        }

        /// <summary>Runs on the main thread. Chunk streaming and block-change deltas are routed to the
        /// apply thread (preserving their arrival order so a delta never races ahead of the chunk it
        /// targets); cheap entity/login packets are handled inline.</summary>
        private void HandlePacket(Packet packet)
        {
            switch (packet)
            {
                case LoginAcceptPacket accept:
                    LocalEntityId = accept.EntityId;
                    SpawnPosition = accept.Spawn;
                    SpawnReceived = true;
                    break;
                case PlayerReadyPacket _:
                    Ready = true;
                    break;
                case DimensionChangePacket dim:
                    ResetForDimensionChange(dim);
                    break;
                case ChunkDataPacket chunkData:
                    ChunkTracer.ApplyEnq(chunkData.Position);
                    _applyQueue.Enqueue(packet);
                    Interlocked.Increment(ref _applyQueueDepth);
                    _applySignal.Set();
                    break;
                case BlockChangesPacket _:
                    _applyQueue.Enqueue(packet);
                    Interlocked.Increment(ref _applyQueueDepth);
                    _applySignal.Set();
                    break;
                case LodColumnDataPacket _:
                    _applyQueue.Enqueue(packet);
                    Interlocked.Increment(ref _applyQueueDepth);
                    _applySignal.Set();
                    break;
                case EntitySpawnPacket spawn when spawn.EntityId != LocalEntityId:
                    Entities[spawn.EntityId] = BuildEntity(spawn);
                    break;
                case EntityMovePacket move when move.EntityId != LocalEntityId:
                    // A move for an entity we haven't been told about yet is dropped; its spawn arrives first.
                    if (Entities.TryGetValue(move.EntityId, out var entity))
                        entity.SetInterpTarget(move.Position, move.Pitch, move.Yaw);
                    break;
                case EntityDespawnPacket despawn:
                    Entities.Remove(despawn.EntityId);
                    break;
                case EntityDataPacket data:
                    if (Entities.TryGetValue(data.EntityId, out var dataEntity)) dataEntity.Data = data.Data;
                    break;
                case WorldTimePacket time:
                    _serverTimeSeconds = time.WorldSeconds;
                    _timeSyncClock.Restart();
                    break;
                case InventoryStatePacket inv:
                    // Copy by value (ItemStack is a struct) so we never alias the server's live inventory,
                    // which the packet carries by reference over the loopback transport.
                    Inventory.SelectedHotbar = inv.Inventory.SelectedHotbar;
                    for (var i = 0; i < Inventory.Size; i++)
                        Inventory.Slots[i] = inv.Inventory.Slots[i];
                    for (var i = 0; i < Inventory.ArmorSize; i++)
                        Inventory.Armor[i] = inv.Inventory.Armor[i];
                    break;
                case ContainerStatePacket state:
                    ApplyContainerState(state);
                    break;
                case PlayerStatsPacket stats:
                    Health = stats.Health;
                    MaxHealth = stats.MaxHealth;
                    Hunger = stats.Hunger;
                    Saturation = stats.Saturation;
                    GameMode = (GameMode) stats.GameMode;
                    PlayerDead = stats.Dead;
                    StatsReceived = true;
                    break;
            }
        }

        /// <summary>Tears the whole cached world down for a dimension transfer: parks the apply thread, discards
        /// its queued (old-dimension) packets, then drops every chunk + LOD region through the same main-thread
        /// paths normal eviction uses (safe alongside the mesh threads), clears entities/containers, and resets
        /// the join gates. The host re-enters loading via <see cref="ConsumeDimensionChange"/>; the new spawn
        /// arrives in a following <see cref="LoginAcceptPacket"/>. Main-thread only (runs inside HandlePacket).</summary>
        private void ResetForDimensionChange(DimensionChangePacket dim)
        {
            _resetting = true;
            _applySignal.Set();
            while (!_applyParked && !_stopped) Thread.Sleep(1);

            while (_applyQueue.TryDequeue(out _)) Interlocked.Decrement(ref _applyQueueDepth);

            foreach (var pos in new List<Vector3i>(LoadedChunks.Keys)) UnloadChunk(pos);
            foreach (var key in new List<Vector3i>(LodRegions.Keys)) UnloadLodRegion(key);

            // Drop any decoded-but-not-yet-rendered LOD regions too (UnloadLodRegion only covered the rendered
            // ones), so no stale horizon survives the dimension switch.
            var lodKeys = new List<Vector3i>();
            LodStore.SnapshotKeys(lodKeys);
            foreach (var key in lodKeys) LodStore.RemoveRegion(key);

            while (_renderReady.TryDequeue(out _)) Interlocked.Decrement(ref _renderReadyDepth);
            while (_lodRenderReady.TryDequeue(out _)) Interlocked.Decrement(ref _lodRenderReadyDepth);

            lock (_meshLock)
            {
                _meshQueueHigh.Clear();
                _meshQueueLow.Clear();
                _meshPending.Clear();
                _lodMeshQueue.Clear();
                _lodMeshPending.Clear();
                _meshQueueDepth = 0;
            }
            lock (_uploadLock)
            {
                _uploadQueue.Clear();
                _uploadPending.Clear();
                _lodUploadQueue.Clear();
                _lodUploadPending.Clear();
                _uploadQueueDepth = 0;
            }

            Entities.Clear();
            Containers.Clear();

            SpawnReceived = false;
            Ready = false;
            HasSky = dim.HasSky;
            FogColor = dim.FogColor;
            AmbientLight = dim.AmbientLight;
            _lastEvictChunk = new Vector3i(int.MinValue);
            _lastLodEvictChunk = new Vector3i(int.MinValue);
            _lastLodStepChunk = new Vector3i(int.MinValue);
            _dimensionChanged = true;

            _resetting = false;
        }

        /// <summary>One-shot read by the host (StateWorld) each frame: returns true exactly once after a
        /// dimension change so it can re-enter its loading screen for the destination.</summary>
        public bool ConsumeDimensionChange()
        {
            if (!_dimensionChanged) return false;
            _dimensionChanged = false;
            return true;
        }

        // Copies a container snapshot into the local view by value (ItemStack is a struct; the loopback
        // transport carries the server's live arrays by reference, so we must not alias them).
        private void ApplyContainerState(ContainerStatePacket state)
        {
            if (!Containers.TryGetValue(state.Position, out var view) ||
                view.Slots.Length != state.Slots.Length || view.Fields.Length != state.Fields.Length)
            {
                view = new ContainerView(state.Slots.Length, state.Fields.Length);
                Containers[state.Position] = view;
            }

            for (var i = 0; i < state.Slots.Length; i++) view.Slots[i] = state.Slots[i];
            for (var i = 0; i < state.Fields.Length; i++) view.Fields[i] = state.Fields[i];
        }

        /// <summary>Builds the client-side entity for a spawn packet: a bare <see cref="Entity"/> for a remote
        /// player (the reserved <see cref="EntityType.PlayerTypeId"/>), an <see cref="EntityItem"/> carrying the
        /// stack for a dropped item, or a typed <see cref="Entity"/> for a creature. The client never ticks these
        /// (the server is authoritative); it only interpolates + renders them.</summary>
        private static Entity BuildEntity(EntitySpawnPacket spawn)
        {
            Entity entity;
            if (spawn.TypeId == EntityType.PlayerTypeId || !GameRegistry.TryGetEntityType(spawn.TypeId, out var type))
                entity = new Entity();
            else if (type.Kind == EntityKind.Item)
                entity = new EntityItem {Type = type, Stack = spawn.Stack};
            else if (type.Kind == EntityKind.FallingBlock)
                entity = new EntityFallingBlock {Type = type, BlockId = (spawn.Data as FallingBlockData)?.BlockId ?? 0};
            else
                entity = new Entity {Type = type};

            entity.EntityId = spawn.EntityId;
            entity.Position = spawn.Position;
            entity.Pitch = spawn.Pitch;
            entity.Yaw = spawn.Yaw;
            entity.Data = spawn.Data;
            return entity;
        }

        private void ApplyThread()
        {
            while (!_stopped)
            {
                if (_resetting)
                {
                    // Park while the main thread tears the world down for a dimension change (it is the sole
                    // writer of LoadedChunks/LodStore, so it must stand still while they're cleared).
                    _applyParked = true;
                    while (_resetting && !_stopped) Thread.Sleep(1);
                    _applyParked = false;
                    continue;
                }

                if (!_applyQueue.TryDequeue(out var packet))
                {
                    _applySignal.WaitOne(50);
                    continue;
                }

                Interlocked.Decrement(ref _applyQueueDepth);

                var allocStart = GC.GetAllocatedBytesForCurrentThread();
                var applyStart = Stopwatch.GetTimestamp();
                try
                {
                    switch (packet)
                    {
                        case ChunkDataPacket chunkData:
                            ApplyChunk(chunkData);
                            break;
                        case BlockChangesPacket changes:
                            ApplyBlockChanges(changes);
                            break;
                        case LodColumnDataPacket lod:
                            ApplyLodColumn(lod);
                            break;
                    }
                }
                catch (Exception e)
                {
                    // A malformed/corrupt chunk packet must not kill the apply thread (which would
                    // silently stop all further chunk decoding); drop it and carry on.
                    Logger.Error("Error applying chunk packet");
                    Logger.Exception(e);
                }
                Profiler.AddApplyTicks(Stopwatch.GetTimestamp() - applyStart);
                Profiler.AddApplyAlloc(GC.GetAllocatedBytesForCurrentThread() - allocStart);
            }
        }

        /// <summary>Apply-thread: builds the chunk (loopback clone, or multiplayer decompress +
        /// deserialize), publishes it, and hands its position plus loaded neighbours to the main thread
        /// for GL render-data creation and meshing.</summary>
        private void ApplyChunk(ChunkDataPacket packet)
        {
            var position = packet.Position;
            ChunkTracer.ApplyStart(position);

            // Loopback carries the live server chunk by reference (clone it, no serialization); a TCP
            // receive carries the still-compressed bytes to decompress and deserialize here. See ChunkDataPacket.
            Chunk chunk;
            if (packet.Chunk != null)
                chunk = new Chunk(this, packet.Chunk);
            else
                using (var reader = new BinaryReader(new MemoryStream(CompressionHelper.DecompressBytes(packet.CompressedData))))
                    chunk = new Chunk(new CachedChunk(this, position, reader));

            // A chunk we already have is a resend (block-data change), so mesh it and its neighbours at
            // high priority; a brand-new chunk is first-load filler.
            var isUpdate = LoadedChunks.ContainsKey(position);
            LoadedChunks[position] = chunk;
            if (!isUpdate) Interlocked.Increment(ref _loadedChunkCount);

            ChunkTracer.Applied(position);
            Profiler.IncApplied();

            EnqueueRenderReady(position, isUpdate);
            foreach (var offset in NeighbourOffsets)
                if (LoadedChunks.ContainsKey(position + offset))
                    EnqueueRenderReady(position + offset, isUpdate);
        }

        /// <summary>Apply-thread: decodes one LOD region (loopback clone or TCP decompress + deserialize),
        /// publishes it to the client LOD store (sole writer), and hands its key to the main thread for GL
        /// render-data creation + meshing. No GL, no chunk/light state touched.</summary>
        private void ApplyLodColumn(LodColumnDataPacket packet)
        {
            LodColumn region;
            if (packet.LodColumn != null)
                // Loopback: a published LOD region is IMMUTABLE (filled once, never mutated — unlike a Chunk),
                // so the client shares the server's array by reference instead of cloning it. This is the big
                // allocation win for a deep/fine horizon (no 32 KB clone per streamed region → no GC churn /
                // hitch as regions stream and re-stream while moving). The server only ever replaces a region
                // with a new object, never mutates a live one (see LodColumnStore), so the shared array is safe.
                region = packet.LodColumn;
            else
                using (var reader = new BinaryReader(new MemoryStream(CompressionHelper.DecompressBytes(packet.CompressedData))))
                    region = new LodColumn(reader, packet.Position);

            LodStore.PutRegion(region);
            _lodRenderReady.Enqueue(packet.Position);
            Interlocked.Increment(ref _lodRenderReadyDepth);
        }

        /// <summary>Enqueues a position for the main-thread GL render-data step, keeping the lock-free
        /// <see cref="RenderReadyQueueDepth"/> mirror in sync. Apply-thread only (the sole producer).</summary>
        private void EnqueueRenderReady(Vector3i position, bool highPriority)
        {
            _renderReady.Enqueue((position, highPriority));
            Interlocked.Increment(ref _renderReadyDepth);
        }

        /// <summary>Apply-thread: applies a batch of per-block deltas (edits + light) into an
        /// already-cached chunk, mutating it in place — no decompress/deserialize. Remeshes the chunk at
        /// high priority, plus any face neighbour a changed boundary block touches (for cross-chunk face
        /// culling).</summary>
        private void ApplyBlockChanges(BlockChangesPacket packet)
        {
            if (!LoadedChunks.TryGetValue(packet.ChunkPos, out var chunk)) return;

            var touchedNeighbours = 0;
            foreach (var change in packet.Changes)
            {
                var local = Chunk.FromIndex(change.LocalIndex);
                chunk.SetBlock(local, change.BlockId);
                chunk.SetLightLevel(local, LightLevel.FromBinary(change.Light));
                chunk.SetSkyLight(local, change.Sky);

                if (local.X == 0) touchedNeighbours |= 1 << 0;
                else if (local.X == Chunk.Size - 1) touchedNeighbours |= 1 << 1;
                if (local.Y == 0) touchedNeighbours |= 1 << 2;
                else if (local.Y == Chunk.Size - 1) touchedNeighbours |= 1 << 3;
                if (local.Z == 0) touchedNeighbours |= 1 << 4;
                else if (local.Z == Chunk.Size - 1) touchedNeighbours |= 1 << 5;
            }

            ChunkTracer.EditApplied(packet.ChunkPos);
            EnqueueRenderReady(packet.ChunkPos, true);

            for (var i = 0; i < NeighbourOffsets.Length; i++)
            {
                if ((touchedNeighbours & (1 << i)) == 0) continue;

                var neighbour = packet.ChunkPos + NeighbourOffsets[i];
                if (LoadedChunks.ContainsKey(neighbour)) EnqueueRenderReady(neighbour, true);
            }
        }

        private void UnloadChunk(Vector3i position)
        {
            ChunkTracer.Abandon(position);
            if (LoadedChunks.TryRemove(position, out _)) Interlocked.Decrement(ref _loadedChunkCount);
            if (RenderData.TryRemove(position, out var renderData))
            {
                RemoveFromRenderList(renderData);
                _disposeQueue.Enqueue(renderData);
                Interlocked.Increment(ref _disposeQueueDepth);
            }

            foreach (var offset in NeighbourOffsets)
                if (LoadedChunks.ContainsKey(position + offset))
                    QueueMesh(position + offset, false);
        }

        /// <summary>Swap-removes a render-data entry from <see cref="RenderList"/> in O(1): the last entry
        /// fills the freed slot and has its index updated. Main-thread only.</summary>
        private void RemoveFromRenderList(ChunkRenderData renderData)
        {
            var index = renderData.RenderListIndex;
            if (index < 0) return;

            var last = RenderList.Count - 1;
            var moved = RenderList[last];
            RenderList[index] = moved;
            moved.RenderListIndex = index;
            RenderList.RemoveAt(last);
            renderData.RenderListIndex = -1;
        }

        /// <summary>Drops LOD regions the player has moved out of <see cref="LodCacheDistance"/> of. Gated on a
        /// chunk-border crossing (regions are large and far, so this rarely fires). Main-thread only.</summary>
        private void EvictDistantLod()
        {
            if (_lodCacheDistanceSq <= 0) return;
            var playerChunk = ChunkInWorld(_playerPosition.ToVector3i());
            if (playerChunk == _lastLodEvictChunk) return;
            _lastLodEvictChunk = playerChunk;

            _lodEvictScratch.Clear();
            for (var i = 0; i < LodRenderList.Count; i++)
            {
                var rd = LodRenderList[i];
                var dx = rd.Middle.X - _playerPosition.X;
                var dz = rd.Middle.Z - _playerPosition.Z;
                if (dx * dx + dz * dz > _lodCacheDistanceSq) _lodEvictScratch.Add(rd.RegionKey);
            }
            foreach (var key in _lodEvictScratch) UnloadLodRegion(key);
        }

        private void UnloadLodRegion(Vector3i key)
        {
            // The client LOD store is lock-based (not the lock-free paletted containers the single-writer rule
            // guards), so a main-thread removal here is safe alongside the apply thread's PutRegion.
            LodStore.RemoveRegion(key);
            if (LodRegions.TryRemove(key, out var lodData))
            {
                RemoveFromLodRenderList(lodData);
                _lodDisposeQueue.Enqueue(lodData);
            }
        }

        private void RemoveFromLodRenderList(LodRenderData lodData)
        {
            var index = lodData.RenderListIndex;
            if (index < 0) return;

            var last = LodRenderList.Count - 1;
            var moved = LodRenderList[last];
            LodRenderList[index] = moved;
            moved.RenderListIndex = index;
            LodRenderList.RemoveAt(last);
            lodData.RenderListIndex = -1;
        }

        /// <summary>Re-queues a chunk whose <see cref="ChunkRenderData.TryUpload"/> found the mesh thread
        /// mid-remesh, so it uploads on a later frame. The mesh thread also re-enqueues on remesh
        /// completion; the pending set dedups so the chunk sits in the queue at most once.</summary>
        private void RequeueUpload(Vector3i position)
        {
            lock (_uploadLock)
            {
                if (_uploadPending.Add(position))
                    _uploadQueue.Enqueue(position);
                _uploadQueueDepth = _uploadQueue.Count;
            }
        }

        private void QueueMesh(Vector3i position, bool highPriority)
        {
            lock (_meshLock)
            {
                var fresh = _meshPending.Add(position);
                if (highPriority)
                    _meshQueueHigh.Enqueue(position);
                else if (fresh)
                    _meshQueueLow.Enqueue(position);
                _meshQueueDepth = _meshPending.Count;
            }
        }

        private void MeshThread(bool lodFirst)
        {
            while (!_stopped)
            {
                // A LOD-first worker drains the horizon queue first and only helps with chunks when no LOD work
                // remains; a chunk-first worker is the reverse (near terrain is the priority). Either way the
                // worker falls through to the other queue rather than idling, then sleeps only when both are dry.
                if (lodFirst)
                {
                    if (TryMeshOneLod()) continue;
                    if (TryMeshOneChunk()) continue;
                }
                else
                {
                    if (TryMeshOneChunk()) continue;
                    if (TryMeshOneLod()) continue;
                }

                Thread.Sleep(2);
            }
        }

        /// <summary>Dequeues and meshes one pending detail chunk (high queue before low). Returns false when no
        /// chunk work is pending. Mesh-pool worker only.</summary>
        private bool TryMeshOneChunk()
        {
            Vector3i position = default;
            var found = false;
            lock (_meshLock)
            {
                if (_meshQueueHigh.Count > 0)
                {
                    position = _meshQueueHigh.Dequeue();
                    found = true;
                }
                else if (_meshQueueLow.Count > 0)
                {
                    position = _meshQueueLow.Dequeue();
                    found = true;
                }

                // Drop duplicates: a promoted position may sit in both queues; only the first
                // dequeue (which clears the pending flag) does the work.
                if (found && !_meshPending.Remove(position)) found = false;
                _meshQueueDepth = _meshPending.Count;
            }

            if (!found) return false;
            MeshChunk(position);
            return true;
        }

        private void MeshChunk(Vector3i position)
        {
            if (!RenderData.TryGetValue(position, out var renderData)) return;
            if (!LoadedChunks.TryGetValue(position, out var chunk)) return;

            var allocStart = GC.GetAllocatedBytesForCurrentThread();
            var meshStart = Stopwatch.GetTimestamp();
            ChunkTracer.MeshStart(position);
            renderData.Chunk = chunk;
            renderData.Update();
            ChunkTracer.MeshDone(position);
            Profiler.AddMeshTicks(Stopwatch.GetTimestamp() - meshStart);
            Profiler.AddMeshAlloc(GC.GetAllocatedBytesForCurrentThread() - allocStart);
            Profiler.IncMeshed();

            lock (_uploadLock)
            {
                if (_uploadPending.Add(position))
                    _uploadQueue.Enqueue(position);
                _uploadQueueDepth = _uploadQueue.Count;
            }
        }

        /// <summary>Mesh-pool worker: meshes one queued LOD region from the streamed run-list (CPU only, no GL).
        /// One worker per region via the _lodMeshPending claim. Returns true if it found a region to consider
        /// (so the worker doesn't idle while LOD work remains).</summary>
        private bool TryMeshOneLod()
        {
            Vector3i key;
            bool claimed;
            lock (_meshLock)
            {
                if (_lodMeshQueue.Count == 0) return false;
                key = _lodMeshQueue.Dequeue();
                claimed = _lodMeshPending.Remove(key);
            }
            if (!claimed) return true;
            if (!LodRegions.TryGetValue(key, out var lodData)) return true;

            lodData.Update(LodStore);

            lock (_uploadLock)
                if (_lodUploadPending.Add(key))
                    _lodUploadQueue.Enqueue(key);
            return true;
        }

        public override void SetBlock(int x, int y, int z, Block block, bool update, bool lowPriority)
            => _connection.Send(new PlaceBlockRequestPacket {Position = new Vector3i(x, y, z), BlockId = block.Id});

        public override void PlaceBlock(EntityPlayer player, Vector3i blockPos, Block block, int metadata)
            => _connection.Send(new PlaceBlockRequestPacket {Position = blockPos, BlockId = block.Id, Metadata = metadata});

        public override Block GetBlock(int x, int y, int z)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            return LoadedChunks.TryGetValue(chunkInWorld, out var chunk)
                ? GameRegistry.GetBlock(chunk.GetBlock(blockInChunk))
                : BlockRegistry.BlockAir;
        }

        public override LightLevel GetBlockLightLevel(int x, int y, int z)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            return LoadedChunks.TryGetValue(chunkInWorld, out var chunk)
                ? chunk.GetLightLevel(blockInChunk)
                : LightLevel.Zero;
        }

        public override BlockData GetBlockData(int x, int y, int z)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            return LoadedChunks.TryGetValue(chunkInWorld, out var chunk) ? chunk.GetBlockData(blockInChunk) : null;
        }

        public override void SetBlockData(int x, int y, int z, BlockData data)
        {
            // Block data is authored server-side; the client only reads what it streams.
        }

        public override void SetBlockLightLevel(int x, int y, int z, LightLevel lightLevel)
        {
            // Lighting is computed server-side and arrives baked into chunk data.
        }

        public override int GetSkyLight(int x, int y, int z)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            // Unloaded ⇒ open sky: all-air chunks above terrain are IsEmpty and never streamed, so a
            // missing chunk means the position sees the sky. Without this the mesher would sample 0 for a
            // surface block's air neighbour in the (empty, unsent) chunk above and render its top dark.
            return LoadedChunks.TryGetValue(chunkInWorld, out var chunk)
                ? chunk.GetSkyLight(blockInChunk)
                : LightLevel.SkyMax;
        }

        public override void SetSkyLight(int x, int y, int z, int level)
        {
            // Sky light is computed server-side and arrives baked into chunk data.
        }
    }
}

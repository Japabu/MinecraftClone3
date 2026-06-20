using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
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

        // Main-thread mirror of RenderData's values, iterated by the renderer each frame in place of
        // enumerating the ConcurrentDictionary (a per-frame O(bucket) scan + enumerator allocation a
        // trace showed dominating the render thread). Kept in sync O(1) on add (DrainRenderReady) and
        // swap-removal on evict (UnloadChunk) via ChunkRenderData.RenderListIndex. Only the main thread
        // touches this list — the mesh thread looks chunks up through RenderData directly.
        public readonly List<ChunkRenderData> RenderList = new List<ChunkRenderData>();

        public readonly Dictionary<int, Entity> Entities = new Dictionary<int, Entity>();

        public int LocalEntityId = -1;

        public Vector3 SpawnPosition;
        public bool SpawnReceived;
        public bool Ready;

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

        public WorldClient(IConnection connection)
        {
            _connection = connection;

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
            _meshThreads = new Thread[meshWorkers];
            for (var i = 0; i < meshWorkers; i++)
            {
                _meshThreads[i] = new Thread(MeshThread) {Name = $"Client Mesh Thread {i}", IsBackground = true};
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
                    if (renderData.TryUpload())
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

            LastUploadChunks = uploadChunks;
            LastUploadIndices = uploadIndices;
            LastUploadMs = _phaseTimer.Elapsed.TotalMilliseconds;

            _phaseTimer.Restart();
            EvictDistantChunks();
            LastEvictMs = _phaseTimer.Elapsed.TotalMilliseconds;

            while (_disposeQueue.TryDequeue(out var disposed))
            {
                Interlocked.Decrement(ref _disposeQueueDepth);
                disposed.Dispose();
            }
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

        public void Disconnect()
        {
            _stopped = true;
            _applySignal.Set();
            _connection.Close();
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
                case EntitySpawnPacket spawn when spawn.EntityId != LocalEntityId:
                    Entities[spawn.EntityId] = new Entity
                    {
                        Position = spawn.Position,
                        Pitch = spawn.Pitch,
                        Yaw = spawn.Yaw
                    };
                    break;
                case EntityMovePacket move when move.EntityId != LocalEntityId:
                    if (!Entities.TryGetValue(move.EntityId, out var entity))
                        Entities[move.EntityId] = entity = new Entity();
                    entity.Position = move.Position;
                    entity.Pitch = move.Pitch;
                    entity.Yaw = move.Yaw;
                    break;
                case EntityDespawnPacket despawn:
                    Entities.Remove(despawn.EntityId);
                    break;
            }
        }

        private void ApplyThread()
        {
            while (!_stopped)
            {
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

        private void MeshThread()
        {
            while (!_stopped)
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

                // Sleep OUTSIDE the lock: holding _meshLock across the idle sleep would block the main
                // thread's QueueMesh on every empty poll.
                if (!found)
                {
                    Thread.Sleep(2);
                    continue;
                }

                if (!RenderData.TryGetValue(position, out var renderData)) continue;
                if (!LoadedChunks.TryGetValue(position, out var chunk)) continue;

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

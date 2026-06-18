using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// Client-side replica of the world. Holds chunks streamed from the server, meshes them on a
    /// background thread into <see cref="ChunkRenderData"/>, and uploads/draws on the main thread.
    /// It never generates terrain, touches disk, or runs lighting — the server is authoritative.
    /// </summary>
    public class WorldClient : WorldBase
    {
        private const int MaxUploadsPerTick = 8;

        // Distance (blocks, chunk-centre to player) past which the client drops a cached chunk and
        // tells the server with a ChunkRelease. Must stay comfortably above the server's send range
        // (ServerNetwork.ViewDistance, 256) so a chunk the server just streamed is never evicted —
        // and thus re-requested — the same moment it arrives; the gap is the caching hysteresis that
        // makes walking away and back cost zero bytes.
        private const float CacheDistance = 384f;
        private const float CacheDistanceSq = CacheDistance * CacheDistance;

        private static readonly Vector3i[] NeighbourOffsets =
        {
            new Vector3i(-1, 0, 0), new Vector3i(+1, 0, 0),
            new Vector3i(0, -1, 0), new Vector3i(0, +1, 0),
            new Vector3i(0, 0, -1), new Vector3i(0, 0, +1)
        };

        public readonly ConcurrentDictionary<Vector3i, ChunkRenderData> RenderData =
            new ConcurrentDictionary<Vector3i, ChunkRenderData>();

        public readonly Dictionary<int, Entity> Entities = new Dictionary<int, Entity>();

        public int LocalEntityId = -1;

        /// <summary>Chunks queued for (re)meshing — surfaced for the profiler.</summary>
        public int PendingMeshCount
        {
            get { lock (_meshLock) return _meshPending.Count; }
        }

        private readonly IConnection _connection;
        private readonly Thread _meshThread;

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
        private readonly List<Vector3i> _evictScratch = new List<Vector3i>();

        private bool _stopped;

        public WorldClient(IConnection connection)
        {
            _connection = connection;

            _meshThread = new Thread(MeshThread) {Name = "Client Mesh Thread", IsBackground = true};
            _meshThread.Start();
        }

        public override void Update()
        {
            while (_connection.TryReceive(out var packet))
                HandlePacket(packet);

            //Cap GL uploads per frame so a meshing burst doesn't stall the frame; the rest stay queued.
            var toUpload = new List<Vector3i>(MaxUploadsPerTick);
            lock (_uploadLock)
            {
                while (toUpload.Count < MaxUploadsPerTick && _uploadQueue.Count > 0)
                {
                    var pos = _uploadQueue.Dequeue();
                    _uploadPending.Remove(pos);
                    toUpload.Add(pos);
                }
            }

            foreach (var pos in toUpload)
                if (RenderData.TryGetValue(pos, out var renderData))
                    renderData.Upload();

            EvictDistantChunks();

            while (_disposeQueue.TryDequeue(out var disposed))
                disposed.Dispose();
        }

        /// <summary>Drops chunks the player has moved away from and tells the server it released them.
        /// The client owns chunk lifetime; the server only streams and resends-on-change.</summary>
        private void EvictDistantChunks()
        {
            _evictScratch.Clear();
            foreach (var entry in LoadedChunks)
            {
                var center = (entry.Key * Chunk.Size + new Vector3i(Chunk.Size / 2)).ToVector3();
                if ((center - _playerPosition).LengthSquared > CacheDistanceSq)
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
            _connection.Close();
        }

        private void HandlePacket(Packet packet)
        {
            switch (packet)
            {
                case LoginAcceptPacket accept:
                    LocalEntityId = accept.EntityId;
                    break;
                case ChunkDataPacket chunkData:
                    ApplyChunk(chunkData.Position, chunkData.CompressedData);
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

        private void ApplyChunk(Vector3i position, byte[] compressedData)
        {
            var raw = CompressionHelper.DecompressBytes(compressedData);
            Chunk chunk;
            using (var reader = new BinaryReader(new MemoryStream(raw)))
                chunk = new Chunk(new CachedChunk(this, position, reader));

            LoadedChunks[position] = chunk;

            // A chunk we already have render data for is a resend (block edit or light propagation),
            // so mesh it and its neighbours at high priority; a brand-new chunk is first-load filler.
            var isUpdate = RenderData.TryGetValue(position, out var renderData);
            if (isUpdate)
                renderData.Chunk = chunk;
            else
                RenderData[position] = new ChunkRenderData(chunk);

            QueueMesh(position, isUpdate);
            foreach (var offset in NeighbourOffsets)
                if (LoadedChunks.ContainsKey(position + offset))
                    QueueMesh(position + offset, isUpdate);
        }

        private void UnloadChunk(Vector3i position)
        {
            LoadedChunks.TryRemove(position, out _);
            if (RenderData.TryRemove(position, out var renderData))
                _disposeQueue.Enqueue(renderData);

            foreach (var offset in NeighbourOffsets)
                if (LoadedChunks.ContainsKey(position + offset))
                    QueueMesh(position + offset, false);
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
            }
        }

        private void MeshThread()
        {
            while (!_stopped)
            {
                Vector3i position;
                lock (_meshLock)
                {
                    if (_meshQueueHigh.Count > 0)
                        position = _meshQueueHigh.Dequeue();
                    else if (_meshQueueLow.Count > 0)
                        position = _meshQueueLow.Dequeue();
                    else
                    {
                        Thread.Sleep(2);
                        continue;
                    }

                    // Drop duplicates: a promoted position may sit in both queues; only the first
                    // dequeue (which clears the pending flag) does the work.
                    if (!_meshPending.Remove(position)) continue;
                }

                if (!RenderData.TryGetValue(position, out var renderData)) continue;
                if (!LoadedChunks.TryGetValue(position, out var chunk)) continue;

                var allocStart = GC.GetAllocatedBytesForCurrentThread();
                renderData.Chunk = chunk;
                renderData.Update();
                Profiler.AddMeshAlloc(GC.GetAllocatedBytesForCurrentThread() - allocStart);

                lock (_uploadLock)
                    if (_uploadPending.Add(position))
                        _uploadQueue.Enqueue(position);
            }
        }

        public override void SetBlock(int x, int y, int z, Block block, bool update, bool lowPriority)
            => _connection.Send(new PlaceBlockRequestPacket {Position = new Vector3i(x, y, z), BlockId = block.Id});

        public override void PlaceBlock(EntityPlayer player, Vector3i blockPos, Block block)
            => _connection.Send(new PlaceBlockRequestPacket {Position = blockPos, BlockId = block.Id});

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
    }
}

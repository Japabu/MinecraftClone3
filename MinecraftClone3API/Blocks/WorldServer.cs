using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Blocks
{
    public class WorldServer : WorldBase
    {
        public static readonly TimeSpan ChunkLifetime = TimeSpan.FromSeconds(30);
        private readonly Dictionary<Vector3i, CachedChunk> _chunksReadyToAdd = new Dictionary<Vector3i, CachedChunk>();
        private readonly HashSet<Vector3i> _chunksReadyToRemove = new HashSet<Vector3i>();

        // Thread-safe set: read/written by both the main thread (Update) and the load thread.
        // ConcurrentDictionary used as a set (the value is ignored).
        private readonly ConcurrentDictionary<Vector3i, byte> _populatedChunks = new ConcurrentDictionary<Vector3i, byte>();

        private readonly Queue<Vector3i> _queuedLightUpdates = new Queue<Vector3i>();

        // Reused scratch for the light-propagation BFS. UpdateThread is the sole caller of
        // UpdateLightValues, so these are cleared and refilled per call instead of allocating six
        // capacity-1024 queues plus two dictionaries every time (the top background-thread allocator).
        // The caches memoise every node the BFS *visits* (read or changed); _lightChanged records only
        // the nodes whose light actually changed, so the writeback dirties just those chunks rather than
        // resending the whole visited sphere on every edit. Pre-sized so a large flood doesn't churn the
        // backing arrays (Dictionary.Resize was the entire light-thread cost in a trace).
        private readonly Queue<LightNode> _lightSpreadQueue = new Queue<LightNode>(1024);
        private readonly Queue<LightNode> _lightRemoveQueue = new Queue<LightNode>(1024);
        private readonly Dictionary<Vector3i, LightLevel> _lightLevelCache = new Dictionary<Vector3i, LightLevel>(8192);
        private readonly Dictionary<Vector3i, Block> _lightBlockCache = new Dictionary<Vector3i, Block>(8192);
        private readonly HashSet<Vector3i> _lightChanged = new HashSet<Vector3i>(8192);

        // Chunks whose stored blocks/light changed since the last network flush. The network layer
        // reads and clears this each tick to resend fresh chunk data to clients holding them.
        // ConcurrentDictionary used as a set (the value is ignored).
        public readonly ConcurrentDictionary<Vector3i, byte> DirtyChunks = new ConcurrentDictionary<Vector3i, byte>();

        private readonly Thread _unloadThread;
        private readonly Thread _loadThread;

        private readonly Thread _updateThread;

        public readonly HashSet<EntityPlayer> PlayerEntities = new HashSet<EntityPlayer>();
        public readonly HashSet<Entity> Entities = new HashSet<Entity>();

        private bool _unloaded;

        public WorldServer()
        {
            _unloadThread = new Thread(UnloadThread) {Name = "Unload Thread"};
            _loadThread = new Thread(LoadThread) {Name = "Load Thread"};
            _updateThread = new Thread(UpdateThread) {Name = "Update Thread"};

            _unloadThread.Start();
            _loadThread.Start();
            _updateThread.Start();
        }

        public int ChunksLoadedCount => LoadedChunks.Count;

        public override void SetBlock(int x, int y, int z, Block block, bool update, bool lowPriority)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            if (LoadedChunks.TryGetValue(chunkInWorld, out var chunk))
            {
                if (chunk.GetBlock(blockInChunk) == block.Id) return;
                chunk.SetBlock(blockInChunk, block.Id);
            }
            else
            {
                chunk = new Chunk(this, chunkInWorld);
                chunk.SetBlock(blockInChunk, block.Id);
                LoadedChunks[chunkInWorld] = chunk;
            }

            if (!update) return;

            var pos = new Vector3i(x, y, z);
            lock (_queuedLightUpdates)
            {
                if (!_queuedLightUpdates.Contains(pos)) _queuedLightUpdates.Enqueue(pos);
            }

            MarkChunkAndBoundaryDirty(chunkInWorld, blockInChunk);
        }

        public override Block GetBlock(int x, int y, int z)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            return LoadedChunks.TryGetValue(chunkInWorld, out Chunk chunk)
                ? GameRegistry.GetBlock(chunk.GetBlock(blockInChunk))
                : BlockRegistry.BlockAir;
        }

        public override void SetBlockData(int x, int y, int z, BlockData data)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            if (LoadedChunks.TryGetValue(chunkInWorld, out var chunk))
                chunk.SetBlockData(blockInChunk, data);

            var pos = new Vector3i(x, y, z);
            lock (_queuedLightUpdates)
            {
                if (!_queuedLightUpdates.Contains(pos)) _queuedLightUpdates.Enqueue(pos);
            }

            MarkChunkAndBoundaryDirty(chunkInWorld, blockInChunk);
        }

        public override void SetBlockLightLevel(int x, int y, int z, LightLevel lightLevel)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            if (!LoadedChunks.TryGetValue(chunkInWorld, out var chunk)) return;
            chunk.SetLightLevel(blockInChunk, lightLevel);

            MarkChunkAndBoundaryDirty(chunkInWorld, blockInChunk);
        }

        public override LightLevel GetBlockLightLevel(int x, int y, int z)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            return LoadedChunks.TryGetValue(chunkInWorld, out Chunk chunk)
                ? chunk.GetLightLevel(blockInChunk)
                : LightLevel.Zero;
        }

        public override BlockData GetBlockData(int x, int y, int z)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            return LoadedChunks.TryGetValue(chunkInWorld, out Chunk chunk) ? chunk.GetBlockData(blockInChunk) : null;
        }

        /// <summary>
        /// Flags a chunk (and the neighbours sharing the touched block's faces) as needing a resend
        /// to clients, so cross-chunk face culling and light stay consistent on the client.
        /// </summary>
        private void MarkChunkAndBoundaryDirty(Vector3i chunkInWorld, Vector3i blockInChunk)
        {
            DirtyChunks[chunkInWorld] = 0;

            if (blockInChunk.X == 0)
                DirtyChunks[chunkInWorld + new Vector3i(-1, 0, 0)] = 0;
            else if (blockInChunk.X == Chunk.Size - 1)
                DirtyChunks[chunkInWorld + new Vector3i(+1, 0, 0)] = 0;
            if (blockInChunk.Y == 0)
                DirtyChunks[chunkInWorld + new Vector3i(0, -1, 0)] = 0;
            else if (blockInChunk.Y == Chunk.Size - 1)
                DirtyChunks[chunkInWorld + new Vector3i(0, +1, 0)] = 0;
            if (blockInChunk.Z == 0)
                DirtyChunks[chunkInWorld + new Vector3i(0, 0, -1)] = 0;
            else if (blockInChunk.Z == Chunk.Size - 1)
                DirtyChunks[chunkInWorld + new Vector3i(0, 0, +1)] = 0;
        }

        public override void PlaceBlock(EntityPlayer player, Vector3i blockPos, Block block)
        {
            SetBlock(blockPos, block);
            block.OnPlaced(this, blockPos, player);
        }

        /// <summary>Adds a player whose position drives chunk-loading interest. Mutated only here and
        /// in <see cref="RemovePlayer"/>; the load thread snapshots the set under the same lock.</summary>
        public void AddPlayer(EntityPlayer player)
        {
            lock (PlayerEntities) PlayerEntities.Add(player);
        }

        public void RemovePlayer(EntityPlayer player)
        {
            lock (PlayerEntities) PlayerEntities.Remove(player);
        }

        public override void Update()
        {
            if (_unloaded) return;

            //Update entities
            foreach (var playerEntity in PlayerEntities)
            {
                playerEntity.Update();
            }
            foreach (var entity in Entities)
            {
                entity.Update();
            }

            lock (_chunksReadyToRemove)
            {
                while (_chunksReadyToRemove.Count > 0)
                {
                    var chunkPos = _chunksReadyToRemove.First();

                    if (LoadedChunks.TryRemove(chunkPos, out _))
                        _populatedChunks.TryRemove(chunkPos, out _);

                    _chunksReadyToRemove.Remove(chunkPos);
                }
            }

            lock (_chunksReadyToAdd)
            {
                while (_chunksReadyToAdd.Count > 0)
                {
                    var entry = _chunksReadyToAdd.First();
                    if (LoadedChunks.ContainsKey(entry.Key))
                        Logger.Error("Chunk has already been loaded! " + entry.Key);
                    else
                        LoadedChunks[entry.Key] = new Chunk(entry.Value);
                    _populatedChunks[entry.Key] = 0;
                    _chunksReadyToAdd.Remove(entry.Key);
                }
            }
        }

        public void Unload()
        {
            _unloaded = true;

            Logger.Info("Waiting for threads to finish...");
            while (_loadThread.IsAlive || _unloadThread.IsAlive || _updateThread.IsAlive)
                Thread.Sleep(100);

            Logger.Info("Saving world...");
            foreach (var entry in LoadedChunks)
                WorldSerializer.SaveChunk(entry.Value);
            Logger.Info("World saved");
        }


        private void UpdateThread()
        {
            Vector3i blockPos;

            while (!_unloaded)
            {
                var allocStart = GC.GetAllocatedBytesForCurrentThread();

                while (_queuedLightUpdates.Count > 0)
                {
                    lock (_queuedLightUpdates)
                    {
                        blockPos = _queuedLightUpdates.Dequeue();
                    }

                    UpdateLightValues(blockPos);
                }

                Profiler.AddLightAlloc(GC.GetAllocatedBytesForCurrentThread() - allocStart);
                Thread.Sleep(1);
            }
        }

        private void UnloadThread()
        {
            while (!_unloaded)
            {
                var allocStart = GC.GetAllocatedBytesForCurrentThread();

                List<Chunk> chunksToUnload;

                lock (LoadedChunks)
                {
                    chunksToUnload =
                        LoadedChunks.Where(
                            pair =>
                                DateTime.Now - pair.Value.Time > ChunkLifetime &&
                                !_chunksReadyToRemove.Contains(pair.Key)).Select(pair => pair.Value).ToList();
                }

                foreach (var chunk in chunksToUnload)
                {
                    WorldSerializer.SaveChunk(chunk);
                    lock (_chunksReadyToRemove)
                    {
                        _chunksReadyToRemove.Add(chunk.Position);
                    }
                }

                Profiler.AddUnloadAlloc(GC.GetAllocatedBytesForCurrentThread() - allocStart);
                Thread.Sleep(1000);
            }
        }

        private void LoadThread()
        {
            while (!_unloaded)
            {
                var allocStart = GC.GetAllocatedBytesForCurrentThread();

                List<EntityPlayer> players;
                lock (PlayerEntities) players = PlayerEntities.ToList();

                var playerChunksLists = new List<List<Vector3i>>();
                foreach (var playerEntity in players)
                {
                    var playerChunksToLoad = new List<Vector3i>();
                    var playerChunk = ChunkInWorld(playerEntity.Position.ToVector3i());

                    //Load 7x7x7 around player
                    for (var x = -3; x <= 3; x++)
                        for (var y = -3; y <= 3; y++)
                            for (var z = -3; z <= 3; z++)
                            {
                                var chunkPos = playerChunk + new Vector3i(x, y, z);
                                if (playerChunksToLoad.Contains(chunkPos)) continue;
                                if (_populatedChunks.ContainsKey(chunkPos) || _chunksReadyToAdd.ContainsKey(chunkPos))
                                {
                                    //Reset chunk time so it will not be unloaded
                                    if (LoadedChunks.TryGetValue(chunkPos, out var chunk)) chunk.Time = DateTime.Now;
                                    continue;
                                }

                                playerChunksToLoad.Add(chunkPos);
                            }


                    //Load 61x3x61 terrain if overworld
                    //heightmap(x*Chunk.Size + Chunk.Size/2, z*Chunk.Size + Chunk.Size/2)

                    var height = 0 + Chunk.Size/2;
                    var heightMapChunkY = ChunkInWorld(0, height, 0).Y;
                    for (var x = -30; x <= 30; x++)
                        for (var y = -1; y <= 1; y++)
                            for (var z = -30; z <= 30; z++)
                            {
                                if (x <= 3 && x >= -3 && y <= 3 && y >= -3 && z <= 3 && z >= -3) continue;

                                var chunkPos = new Vector3i(playerChunk.X + x, heightMapChunkY + y, playerChunk.Z + z);

                                if (_populatedChunks.ContainsKey(chunkPos) || _chunksReadyToAdd.ContainsKey(chunkPos))
                                {
                                    //Reset chunk time so it will not be unloaded
                                    if (LoadedChunks.TryGetValue(chunkPos, out var chunk)) chunk.Time = DateTime.Now;
                                    continue;
                                }

                                playerChunksToLoad.Add(chunkPos);
                            }



                    playerChunksToLoad.Sort(
                        (v0, v1) =>
                            (int) (v0.ToVector3() - playerChunk.ToVector3()).LengthSquared -
                            (int) (v1.ToVector3() - playerChunk.ToVector3()).LengthSquared);

                    //Cap player chunk load tasks to 16
                    if(playerChunksToLoad.Count > 16)
                        playerChunksToLoad.RemoveRange(16, playerChunksToLoad.Count - 16);

                    playerChunksLists.Add(playerChunksToLoad);
                }

                var merged = ExtensionHelper.ZipMerge(playerChunksLists.ToArray());
                foreach (var chunkPos in merged)
                {
                    // Players with overlapping interest produce duplicate positions in the merged
                    // list; skip ones already queued or known so we don't load (or add) them twice.
                    if (_populatedChunks.ContainsKey(chunkPos)) continue;
                    lock (_chunksReadyToAdd)
                        if (_chunksReadyToAdd.ContainsKey(chunkPos)) continue;

                    var cachedChunk = LoadChunk(chunkPos);

                    if (cachedChunk.IsEmpty)
                    {
                        //Empty chunks dont need to be added to LoadedChunks
                        _populatedChunks[chunkPos] = 0;
                    }
                    else
                    {
                        lock (_chunksReadyToAdd)
                        {
                            _chunksReadyToAdd[chunkPos] = cachedChunk;
                        }
                    }
                }

                Profiler.AddLoadAlloc(GC.GetAllocatedBytesForCurrentThread() - allocStart);
                Thread.Sleep(10);
            }
        }


        private struct LightNode
        {
            public readonly Vector3i Position;
            public readonly int Value;

            public LightNode(Vector3i position, int value)
            {
                Position = position;
                Value = value;
            }
        }

        private void UpdateLightValues(Vector3i blockPos)
        {
            var block = GetBlock(blockPos);
            var blockEmittingLightLevel = block.GetLightLevel(this, blockPos);
            var oldBlockLightLevel = GetBlockLightLevel(blockPos);
            var occludingBlock = IsOpaqueFullBlock(blockPos);


            //If the block is not occluding its faces it can accept lighting from nearby blocks
            if (!occludingBlock)
            {
                foreach (var face in BlockFaceHelper.Faces)
                {
                    var neighborLightLevel = GetBlockLightLevel(blockPos + face.GetNormali());
                    for (var color = 0; color < 3; color++)
                    {
                        blockEmittingLightLevel[color] = Math.Max(blockEmittingLightLevel[color],
                            neighborLightLevel[color]);
                    }
                }

                for (var color = 0; color < 3; color++)
                    blockEmittingLightLevel[color] = block.OnLightPassThrough(this, blockPos,
                        blockEmittingLightLevel[color], color);
            }

            var cachedLightLevels = _lightLevelCache;
            cachedLightLevels.Clear();
            cachedLightLevels[blockPos] = blockEmittingLightLevel;

            var cachedBlocks = _lightBlockCache;
            cachedBlocks.Clear();

            var changed = _lightChanged;
            changed.Clear();
            if (blockEmittingLightLevel.Binary != oldBlockLightLevel.Binary) changed.Add(blockPos);

            //foreach color channel
            for (var color = 0; color < 3; color++)
            {
                var spreadQueue = _lightSpreadQueue;
                var removeQueue = _lightRemoveQueue;
                spreadQueue.Clear();
                removeQueue.Clear();

                if (blockEmittingLightLevel[color] > oldBlockLightLevel[color])
                {
                    spreadQueue.Enqueue(new LightNode(blockPos, blockEmittingLightLevel[color]));
                }
                else if (blockEmittingLightLevel[color] < oldBlockLightLevel[color])
                {
                    removeQueue.Enqueue(new LightNode(blockPos, oldBlockLightLevel[color]));
                }

                while (removeQueue.Count > 0)
                {
                    var node = removeQueue.Dequeue();

                    foreach (var face in BlockFaceHelper.Faces)
                    {
                        var nextNode = node.Position + face.GetNormali();

                        //If chunk does not exist stop
                        if (IsBlockInEmptyChunk(nextNode)) continue;

                        //Cache light level if not already cached
                        if (!cachedLightLevels.TryGetValue(nextNode, out var nextNodeLightLevel))
                        {
                            nextNodeLightLevel = GetBlockLightLevel(nextNode);
                            cachedLightLevels[nextNode] = nextNodeLightLevel;
                        }

                        //If the next nodes light level is higher or equal to our value spread light to fill the holes
                        if (nextNodeLightLevel[color] >= node.Value)
                        {
                            spreadQueue.Enqueue(new LightNode(nextNode, node.Value));
                            continue;
                        }
                        //If the next nodes light level is zero stop
                        if (nextNodeLightLevel[color] == 0) continue;

                        //If next node block is an occluder stop
                        if (IsOpaqueFullBlock(nextNode)) continue;

                        //Set next nodes light level and advance
                        nextNodeLightLevel[color] = 0;
                        cachedLightLevels[nextNode] = nextNodeLightLevel;
                        changed.Add(nextNode);

                        if (node.Value - 1 > 0)
                            removeQueue.Enqueue(new LightNode(nextNode, node.Value - 1));
                    }
                }

                while (spreadQueue.Count > 0)
                {
                    var node = spreadQueue.Dequeue();

                    foreach (var face in BlockFaceHelper.Faces)
                    {
                        var nextNode = node.Position + face.GetNormali();

                        //If chunk does not exist stop
                        //TODO: Fix potential bugs
                        if (IsBlockInEmptyChunk(nextNode)) continue;

                        //Cache light level if not already cached
                        if (!cachedLightLevels.TryGetValue(nextNode, out var nextNodeLightLevel))
                        {
                            nextNodeLightLevel = GetBlockLightLevel(nextNode);
                            cachedLightLevels[nextNode] = nextNodeLightLevel;
                        }

                        //Cache block if not already cached
                        if (!cachedBlocks.TryGetValue(nextNode, out var nextNodeBlock))
                        {
                            nextNodeBlock = GetBlock(nextNode);
                            cachedBlocks[nextNode] = nextNodeBlock;
                        }

                        var newValue = nextNodeBlock.OnLightPassThrough(this, nextNode, node.Value, color);

                        //If the next nodes light level is higher or equal to our value stop
                        if (nextNodeLightLevel[color] >= newValue) continue;

                        //If next node block is an occluder stop
                        if (nextNodeBlock.IsOpaqueFullBlock(this, nextNode)) continue;

                        //Set next nodes light level and advance
                        nextNodeLightLevel[color] = newValue;
                        cachedLightLevels[nextNode] = nextNodeLightLevel;
                        changed.Add(nextNode);

                        if(newValue > 0)
                            spreadQueue.Enqueue(new LightNode(nextNode, newValue));
                    }
                }
            }

            foreach (var pos in changed)
            {
                SetBlockLightLevel(pos, cachedLightLevels[pos]);
            }
        }

        private CachedChunk LoadChunk(Vector3i position)
        {
            var chunk = WorldSerializer.LoadChunk(this, position);
            if (chunk != null) return chunk;

            //TODO: implement terrain gen
            chunk = new CachedChunk(this, position);
            var worldMin = position * Chunk.Size;

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {

                var height = OpenSimplexNoise.Generate((worldMin.X + x)*0.06f, (worldMin.Z + z)*0.06f)*5;
                height += OpenSimplexNoise.Generate((worldMin.X + x) * 0.1f, (worldMin.Z + z) * 0.1f)*2;
                //height += OpenSimplexNoise.Generate((worldMin.X + x) * 0.005f, (worldMin.Z + z) * 0.005f) * 10;


                    for (var y = 0; y < Chunk.Size; y++)
                    if (worldMin.Y + y <= height)
                        chunk.SetBlock(x, y, z, (worldMin.Y + y == (int)height) ? GameRegistry.GetBlock("Vanilla:Grass") : GameRegistry.GetBlock("Vanilla:Dirt"));
            }


            return chunk;
        }
    }
}

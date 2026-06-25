using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using MinecraftClone3API.WorldGen;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Blocks
{
    public class WorldServer : WorldBase
    {
        /// <summary>Authoritative world clock: ticks elapsed since the world started, advanced once per
        /// <see cref="Update"/> (20 tps in both the integrated and the dedicated server). Drives the
        /// day/night time sync. Single writer (the tick thread); a plain field read elsewhere is fine.</summary>
        public long TickCount { get; private set; }

        public const double SecondsPerTick = PlayerPhysics.TickSeconds;

        /// <summary>The world clock in seconds (<see cref="TickCount"/> · <see cref="SecondsPerTick"/>);
        /// shipped in <c>WorldTimePacket</c> to drive the client day/night cycle.</summary>
        public double WorldTimeSeconds => TickCount * SecondsPerTick;

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

        // Sky-light BFS scratch, parallel to the block-light caches above. UpdateSkyValues is the sole
        // user and runs sequentially after UpdateLightValues on the same UpdateThread, so it reuses the
        // _lightSpreadQueue/_lightRemoveQueue/_lightChunkCache and keeps only its own value cache + changed
        // set (sky is a single 0..15 scalar, not a packed LightLevel).
        private readonly Dictionary<Vector3i, int> _skyLevelCache = new Dictionary<Vector3i, int>(8192);
        private readonly HashSet<Vector3i> _skyChanged = new HashSet<Vector3i>(8192);

        // Highest a sky-exposure up-scan walks before assuming open sky, so a block placed far above
        // terrain can't scan unboundedly. Terrain is shallow and the loaded vertical band is thin, so the
        // scan almost always exits into an unloaded chunk within a few blocks.
        private const int SkyScanMaxHeight = 256;

        // Memoises chunk lookups (chunk pos -> Chunk, null when absent) for the duration of one
        // UpdateLightValues flood: a torch flood probes LoadedChunks tens of thousands of times for
        // the per-neighbour empty-chunk test, almost all hitting the same few chunks.
        private readonly Dictionary<Vector3i, Chunk> _lightChunkCache = new Dictionary<Vector3i, Chunk>();

        // Signalled whenever a light update is queued so the UpdateThread sleeps instead of spinning
        // Thread.Sleep(1) (1000 idle wakeups/s); the timed wait still lets it observe _unloaded.
        private readonly AutoResetEvent _lightSignal = new AutoResetEvent(false);

        // Reused across LoadThread ticks (the load thread is the sole user) so the per-tick interest
        // scan allocates nothing steady-state: a player snapshot, the per-player candidate lists, a
        // dedup set, the round-robin merge output, and a closure-free distance sort.
        private readonly List<EntityPlayer> _loadPlayersScratch = new List<EntityPlayer>();
        private readonly List<List<Vector3i>> _loadPlayerChunkLists = new List<List<Vector3i>>();
        private readonly HashSet<Vector3i> _loadDedup = new HashSet<Vector3i>();
        private readonly List<Vector3i> _loadMerged = new List<Vector3i>();
        private Vector3i _loadSortOrigin;
        private readonly Comparison<Vector3i> _loadSort;

        // Reused by the unload scan (sole user) so the per-second sweep allocates no result list.
        private readonly List<Chunk> _unloadScratch = new List<Chunk>();

        // Horizontal radius (in chunks) of the load band; kept one chunk past ServerNetwork.ViewDistance
        // (≈ RenderDistance) so the streamer never wants a chunk the load thread hasn't loaded. Default 10
        // (≈ 160 blocks); in singleplayer StateWorld drives it from the render-distance slider, on a dedicated
        // server it stays the default. Read live each scan by the load thread (volatile so a runtime change is
        // seen promptly). The full BedrockY..WorldTop vertical column is loaded within this radius.
        public volatile int TerrainRadius = 10;

        // Chunks generated per LoadThread iteration per player. Large enough that the ~11k-position interest
        // scan amortizes over a big gen batch (the old cap of 16 meant ~275 scans to fill RD16), small enough
        // that a moving player's interest is re-read often enough to follow them. With the no-idle-sleep change
        // (sleep only when nothing is pending) this lets the world stream in flat-out instead of dribbling.
        private const int MaxChunksPerLoad = 128;

        // Cores the parallel chunk-gen batch may use — leave two for the client mesh pool + main/render thread.
        private readonly System.Threading.Tasks.ParallelOptions _genParallelOptions =
            new System.Threading.Tasks.ParallelOptions
                {MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2)};

        // The active world generator (resolved from the Vanilla:Overworld dimension, or a void fallback if
        // no dimension is registered). Sole writer of generated chunks runs on the load thread.
        private readonly IChunkGenerator _generator;

        // Disk persistence bound to this world's directory. Per-instance (dir + index caches) rather than
        // static so each world saves to its own folder; safe because exactly one WorldServer exists per
        // process and only it touches the serializer (the threading model's single-server assumption).
        private readonly WorldSerializer _serializer;

        // Per-block authoritative changes (edits + light propagation) queued for the network layer to
        // flush as compact BlockChanges packets instead of resending whole GZip'd chunks. Fed by the
        // tick thread (SetBlock) and the light Update thread (SetBlockLightLevel); drained each tick by
        // ServerNetwork.FlushBlockChanges. Keyed by absolute block (chunk pos + local index) with
        // last-write-wins: overlapping light floods (rapid breaking re-lights the same cells many times)
        // would otherwise enqueue the same block over and over, so a plain queue grew O(floods × volume)
        // and trapped the flush thread; deduping bounds pending changes to O(distinct changed blocks) and
        // is correct because each BlockChange is a full (id, light) snapshot the client applies idempotently.
        public readonly ConcurrentDictionary<(Vector3i Chunk, ushort Index), BlockChange> BlockChanges =
            new ConcurrentDictionary<(Vector3i, ushort), BlockChange>();

        // Chunks whose block *data* changed since the last network flush. Block data is not carried by
        // BlockChanges deltas (only id + light are), so a data change still triggers a whole-chunk
        // resend. The network layer reads and clears this each tick. ConcurrentDictionary used as a set.
        public readonly ConcurrentDictionary<Vector3i, byte> DirtyChunks = new ConcurrentDictionary<Vector3i, byte>();

        // Block positions whose block requests a per-tick server update (Block.NeedsServerTick — e.g. furnaces).
        // Maintained as a set on SetBlock, on chunk load (scanning loaded block data), and chunk unload; ticked
        // each Update on the tick thread. Far cheaper than scanning every loaded block every tick.
        private readonly ConcurrentDictionary<Vector3i, byte> _tickingBlocks = new ConcurrentDictionary<Vector3i, byte>();

        private readonly Thread _unloadThread;
        private readonly Thread _loadThread;

        private readonly Thread _updateThread;

        // Phase-2 distant-horizon LOD. A separate low-priority thread fills _lodStore with cheap surface-only
        // columns in the ring BEYOND the real-chunk TerrainRadius, out to LodRadius. It is the SOLE writer of
        // _lodStore (single-writer invariant for the new container) and only READS LoadedChunks/PlayerEntities;
        // it never touches chunk storage, light, or the dirty/staging sets. LodRadius defaults to TerrainRadius
        // so the whole LOD system is dormant (empty ring → no work, no stream, no draw) until StateWorld raises
        // it from the render-distance config. Volatile: read live by the LOD thread.
        private readonly Thread _lodThread;
        private readonly LodColumnStore _lodStore = new LodColumnStore();
        public LodColumnStore LodStore => _lodStore;
        public volatile int LodRadius = 10;
        private readonly List<EntityPlayer> _lodPlayersScratch = new List<EntityPlayer>();
        private readonly List<Vector3i> _lodKeysScratch = new List<Vector3i>();
        private const int MaxLodRegionsPerIter = 4;
        // How far the LOD fill overlaps INWARD past the real-chunk band. Sized so the LOD store covers the
        // client's cross-fade band [RD - FadeBandWidth, RD] with a full region of margin (a region is 128 wide,
        // ~90 half-diagonal), so the chunks dithering OUT always have horizon to dither IN against — no holes.
        private const int LodInnerOverlap = 160;

        public readonly HashSet<EntityPlayer> PlayerEntities = new HashSet<EntityPlayer>();
        public readonly HashSet<Entity> Entities = new HashSet<Entity>();

        // Entity-id allocator shared by players (ServerNetwork.Login) and world entities (SpawnEntity), so
        // every networked entity has a unique id. Entities are transient, so ids are session-local.
        private int _nextEntityId = 1;

        // Spawns/despawns the network layer hasn't broadcast yet, drained each Pump by ServerNetwork. Mob/item
        // entities are server-authoritative: the world owns their lifetime, the network only relays it.
        public readonly Queue<Entity> PendingSpawns = new Queue<Entity>();
        public readonly Queue<int> PendingDespawns = new Queue<int>();

        // Ambient creature spawning: every so often try to drop a small group near a random player.
        private readonly Random _spawnRng = new Random();
        private int _spawnCooldown = SpawnIntervalTicks;
        private const int SpawnIntervalTicks = 20 * 8;   // ~8 s between spawn attempts
        private const int CreatureCap = 40;              // soft cap on live creatures
        private const float EntityVoidY = -128f;         // despawn entities that fall below this

        private bool _unloaded;

        // Lock-free mirror of _chunksReadyToAdd.Count, updated under lock(_chunksReadyToAdd) on the load
        // thread (stage) and the main thread (drain), so the profiler reads the staging-queue depth
        // without taking the lock or Dictionary.Count.
        private volatile int _stageQueueDepth;
        public int StageQueueDepth => _stageQueueDepth;

        private const string OverworldDimensionKey = "Vanilla:Overworld";

        public Vector3 SpawnPosition => _generator.Spawn().ToVector3();

        public readonly string WorldDir;

        public WorldServer(long seed, string worldDir)
        {
            WorldDir = worldDir;
            _serializer = new WorldSerializer(worldDir);

            _generator = GameRegistry.TryGetDimension(OverworldDimensionKey, out var dimension)
                ? dimension.CreateGenerator(seed)
                : CreateFallbackGenerator();

            _loadSort = (v0, v1) =>
                (int) (v0.ToVector3() - _loadSortOrigin.ToVector3()).LengthSquared -
                (int) (v1.ToVector3() - _loadSortOrigin.ToVector3()).LengthSquared;

            _unloadThread = new Thread(UnloadThread) {Name = "Unload Thread"};
            _loadThread = new Thread(LoadThread) {Name = "Load Thread"};
            _updateThread = new Thread(UpdateThread) {Name = "Update Thread"};
            _lodThread = new Thread(LodThread) {Name = "LOD Thread", Priority = ThreadPriority.BelowNormal};

            _unloadThread.Start();
            _loadThread.Start();
            _updateThread.Start();
            _lodThread.Start();
        }

        private static IChunkGenerator CreateFallbackGenerator()
        {
            Logger.Error($"Dimension \"{OverworldDimensionKey}\" is not registered — generating an empty " +
                         "world. Is the Vanilla plugin loaded?");
            return new FlatChunkGenerator();
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

            var worldPos = new Vector3i(x, y, z);
            if (block.NeedsServerTick) _tickingBlocks[worldPos] = 0;
            else _tickingBlocks.TryRemove(worldPos, out _);

            if (!update) return;

            QueueLightUpdate(new Vector3i(x, y, z));

            EnqueueBlockChange(chunkInWorld, blockInChunk, block.Id, chunk.GetLightLevel(blockInChunk).Binary,
                (ushort) chunk.GetSkyLight(blockInChunk));
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

            QueueLightUpdate(new Vector3i(x, y, z));

            MarkChunkAndBoundaryDirty(chunkInWorld, blockInChunk);
        }

        public override void SetBlockLightLevel(int x, int y, int z, LightLevel lightLevel)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            if (!LoadedChunks.TryGetValue(chunkInWorld, out var chunk)) return;
            chunk.SetLightLevel(blockInChunk, lightLevel);

            EnqueueBlockChange(chunkInWorld, blockInChunk, chunk.GetBlock(blockInChunk), lightLevel.Binary,
                (ushort) chunk.GetSkyLight(blockInChunk));
        }

        public override LightLevel GetBlockLightLevel(int x, int y, int z)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            return LoadedChunks.TryGetValue(chunkInWorld, out Chunk chunk)
                ? chunk.GetLightLevel(blockInChunk)
                : LightLevel.Zero;
        }

        public override void SetSkyLight(int x, int y, int z, int level)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            if (!LoadedChunks.TryGetValue(chunkInWorld, out var chunk)) return;
            chunk.SetSkyLight(blockInChunk, level);

            EnqueueBlockChange(chunkInWorld, blockInChunk, chunk.GetBlock(blockInChunk),
                chunk.GetLightLevel(blockInChunk).Binary, (ushort) level);
        }

        public override int GetSkyLight(int x, int y, int z)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            return LoadedChunks.TryGetValue(chunkInWorld, out Chunk chunk)
                ? chunk.GetSkyLight(blockInChunk)
                : 0;
        }

        /// <summary>Flags the chunk owning a block as needing a save, without the resend/relight a full
        /// <see cref="SetBlockData"/> triggers. Used by ticking blocks (furnaces) that mutate their block data
        /// in place every tick: the change must persist, but it does not affect the mesh or light.</summary>
        public void TouchBlockDataForSave(Vector3i pos)
        {
            if (LoadedChunks.TryGetValue(ChunkInWorld(pos), out var chunk)) chunk.NeedsSaving = true;
        }

        public override BlockData GetBlockData(int x, int y, int z)
        {
            var chunkInWorld = ChunkInWorld(x, y, z);
            var blockInChunk = BlockInChunk(x, y, z);

            return LoadedChunks.TryGetValue(chunkInWorld, out Chunk chunk) ? chunk.GetBlockData(blockInChunk) : null;
        }

        private void EnqueueBlockChange(Vector3i chunkInWorld, Vector3i blockInChunk, ushort blockId, ushort light, ushort sky)
        {
            var localIndex = (ushort) Chunk.Index(blockInChunk.X, blockInChunk.Y, blockInChunk.Z);
            BlockChanges[(chunkInWorld, localIndex)] = new BlockChange(chunkInWorld, localIndex, blockId, light, sky);
        }

        private void QueueLightUpdate(Vector3i pos)
        {
            lock (_queuedLightUpdates)
            {
                if (!_queuedLightUpdates.Contains(pos)) _queuedLightUpdates.Enqueue(pos);
            }

            _lightSignal.Set();
        }

        /// <summary>
        /// Flags a chunk (and the neighbours sharing the touched block's faces) as needing a resend
        /// to clients, so cross-chunk face culling and light stay consistent on the client. Used only
        /// for block-data changes, which BlockChanges deltas do not carry.
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

        public override void PlaceBlock(EntityPlayer player, Vector3i blockPos, Block block, int metadata)
        {
            SetBlock(blockPos, block);
            block.OnPlaced(this, blockPos, player, metadata);
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

        /// <summary>Allocates the next unique networked-entity id (players + world entities share the space).</summary>
        public int NextEntityId() => _nextEntityId++;

        /// <summary>Spawns a world entity (mob/animal/dropped item) at <paramref name="position"/>, assigning its
        /// id and queueing it for the network layer to announce. Tick-thread only.</summary>
        public Entity SpawnEntity(Entity entity, Vector3 position)
        {
            entity.EntityId = NextEntityId();
            entity.ServerWorld = this;
            entity.Position = position;
            Entities.Add(entity);
            PendingSpawns.Enqueue(entity);
            return entity;
        }

        /// <summary>Convenience: spawns one entity of the given registered type.</summary>
        public Entity SpawnEntity(EntityType type, Vector3 position) => SpawnEntity(type.CreateEntity(), position);

        /// <summary>Spawns a dropped-item entity carrying <paramref name="stack"/>, given a registered item
        /// entity type (the first <see cref="EntityKind.Item"/> type). No-op if none is registered.</summary>
        public EntityItem DropItem(ItemStack stack, Vector3 position)
        {
            if (stack.IsEmpty) return null;
            EntityType itemType = null;
            foreach (var type in GameRegistry.EntityTypes)
                if (type.Kind == EntityKind.Item) { itemType = type; break; }
            if (itemType == null) return null;

            var item = (EntityItem) SpawnEntity(itemType, position);
            item.Stack = stack;
            // A little upward + sideways pop so drops scatter instead of stacking on one pixel.
            item.Velocity = new Vector3(
                (float) (_spawnRng.NextDouble() - 0.5) * 0.2f, 0.2f,
                (float) (_spawnRng.NextDouble() - 0.5) * 0.2f);
            return item;
        }

        public override void Update()
        {
            if (_unloaded) return;

            TickCount++;

            //Update entities
            foreach (var playerEntity in PlayerEntities)
            {
                playerEntity.Update();
            }
            foreach (var entity in Entities)
            {
                entity.Update();
                // Despawn anything that fell out of the world (e.g. wandered into an unloaded column and
                // gravity ran unchecked) so it can't tick forever far below the map.
                if (entity.Position.Y < EntityVoidY) entity.Dead = true;
            }

            DrainDeadEntities();
            TrySpawnCreatures();

            lock (_chunksReadyToRemove)
            {
                foreach (var chunkPos in _chunksReadyToRemove)
                {
                    if (LoadedChunks.TryRemove(chunkPos, out _))
                        _populatedChunks.TryRemove(chunkPos, out _);
                    UnregisterTickingBlocks(chunkPos);
                    ChunkTracer.Abandon(chunkPos);
                }

                _chunksReadyToRemove.Clear();
            }

            var drainStart = Stopwatch.GetTimestamp();
            var drained = 0;
            lock (_chunksReadyToAdd)
            {
                foreach (var entry in _chunksReadyToAdd)
                {
                    if (LoadedChunks.ContainsKey(entry.Key))
                        Logger.Error("Chunk has already been loaded! " + entry.Key);
                    else
                    {
                        var chunk = new Chunk(entry.Value);
                        LoadedChunks[entry.Key] = chunk;
                        RegisterTickingBlocks(entry.Key, chunk);
                    }
                    _populatedChunks[entry.Key] = 0;
                    ChunkTracer.Published(entry.Key);
                    drained++;
                }

                _chunksReadyToAdd.Clear();
                _stageQueueDepth = 0;
            }
            Profiler.AddDrainAddTime((Stopwatch.GetTimestamp() - drainStart) * 1000.0 / Stopwatch.Frequency);
            Profiler.AddDrainAddCount(drained);

            TickBlocks();
        }

        // Register the freshly-loaded chunk's ticking blocks (those whose persisted block data marks a
        // Block.NeedsServerTick block, e.g. a furnace), so the server resumes ticking them after a load.
        private void RegisterTickingBlocks(Vector3i chunkInWorld, Chunk chunk)
        {
            foreach (var blockInChunk in chunk.BlockDataPositions)
            {
                var pos = chunkInWorld * Chunk.Size + blockInChunk;
                if (GetBlock(pos.X, pos.Y, pos.Z).NeedsServerTick) _tickingBlocks[pos] = 0;
            }
        }

        private void UnregisterTickingBlocks(Vector3i chunkInWorld)
        {
            foreach (var pos in _tickingBlocks.Keys)
                if (ChunkInWorld(pos) == chunkInWorld) _tickingBlocks.TryRemove(pos, out _);
        }

        private void TickBlocks()
        {
            foreach (var pos in _tickingBlocks.Keys)
            {
                var block = GetBlock(pos.X, pos.Y, pos.Z);
                if (block.NeedsServerTick) block.OnServerTick(this, pos);
                else _tickingBlocks.TryRemove(pos, out _);
            }
        }

        /// <summary>Removes entities flagged <see cref="Entity.Dead"/> (despawn timeout, item pickup, death)
        /// and queues their ids for the network despawn broadcast.</summary>
        private void DrainDeadEntities()
        {
            if (Entities.Count == 0) return;

            Entities.RemoveWhere(entity =>
            {
                if (!entity.Dead) return false;
                PendingDespawns.Enqueue(entity.EntityId);
                return true;
            });
        }

        /// <summary>Ambient creature spawning: periodically tries to place a small group of a random creature
        /// type on the ground near a random player, up to a soft cap. No-op when no creature type is registered
        /// or no player is online.</summary>
        private void TrySpawnCreatures()
        {
            if (--_spawnCooldown > 0) return;
            _spawnCooldown = SpawnIntervalTicks;

            if (Entities.Count >= CreatureCap) return;

            EntityPlayer anchor;
            lock (PlayerEntities)
            {
                if (PlayerEntities.Count == 0) return;
                var skip = _spawnRng.Next(PlayerEntities.Count);
                anchor = null;
                foreach (var p in PlayerEntities) { if (skip-- == 0) { anchor = p; break; } }
            }
            if (anchor == null) return;

            var creatureTypes = new List<EntityType>();
            foreach (var type in GameRegistry.EntityTypes)
                if (type.Kind == EntityKind.Creature) creatureTypes.Add(type);
            if (creatureTypes.Count == 0) return;

            var chosen = creatureTypes[_spawnRng.Next(creatureTypes.Count)];
            var group = 1 + _spawnRng.Next(3);
            for (var i = 0; i < group; i++)
            {
                var ox = _spawnRng.Next(-24, 25);
                var oz = _spawnRng.Next(-24, 25);
                var baseX = (int) MathF.Round(anchor.Position.X) + ox;
                var baseZ = (int) MathF.Round(anchor.Position.Z) + oz;
                if (!TryFindGround(baseX, (int) MathF.Round(anchor.Position.Y), baseZ, out var groundY)) continue;
                SpawnEntity(chosen, new Vector3(baseX + 0.5f, groundY, baseZ + 0.5f));
            }
        }

        /// <summary>Scans for a standable surface (solid block with two air blocks above) near <paramref
        /// name="aroundY"/>, returning the feet Y. False if the column isn't loaded or no surface is found.</summary>
        private bool TryFindGround(int x, int aroundY, int z, out int feetY)
        {
            feetY = 0;
            for (var y = aroundY + 16; y >= aroundY - 16; y--)
            {
                if (!IsOpaqueFullBlock(new Vector3i(x, y, z))) continue;
                if (GetBlock(x, y + 1, z) != BlockRegistry.BlockAir) continue;
                if (GetBlock(x, y + 2, z) != BlockRegistry.BlockAir) continue;
                feetY = y + 1;
                return true;
            }

            return false;
        }

        public void Unload()
        {
            _unloaded = true;

            Logger.Info("Waiting for threads to finish...");
            while (_loadThread.IsAlive || _unloadThread.IsAlive || _updateThread.IsAlive || _lodThread.IsAlive)
                Thread.Sleep(100);

            Logger.Info("Saving world...");
            foreach (var entry in LoadedChunks)
                _serializer.SaveChunk(entry.Value);
            Logger.Info("World saved");
        }


        private void UpdateThread()
        {
            while (!_unloaded)
            {
                var allocStart = GC.GetAllocatedBytesForCurrentThread();

                var didWork = false;
                while (true)
                {
                    Vector3i blockPos;
                    lock (_queuedLightUpdates)
                    {
                        if (_queuedLightUpdates.Count == 0) break;
                        blockPos = _queuedLightUpdates.Dequeue();
                    }

                    UpdateLightValues(blockPos);
                    UpdateSkyValues(blockPos);
                    didWork = true;
                }

                Profiler.AddLightAlloc(GC.GetAllocatedBytesForCurrentThread() - allocStart);

                if (!didWork) _lightSignal.WaitOne(100);
            }
        }

        private void UnloadThread()
        {
            while (!_unloaded)
            {
                var allocStart = GC.GetAllocatedBytesForCurrentThread();

                var now = DateTime.Now;
                _unloadScratch.Clear();
                foreach (var pair in LoadedChunks)
                    if (now - pair.Value.Time > ChunkLifetime)
                        _unloadScratch.Add(pair.Value);

                foreach (var chunk in _unloadScratch)
                {
                    lock (_chunksReadyToRemove)
                        if (!_chunksReadyToRemove.Add(chunk.Position)) continue;

                    _serializer.SaveChunk(chunk);
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

                _loadPlayersScratch.Clear();
                lock (PlayerEntities)
                    foreach (var player in PlayerEntities) _loadPlayersScratch.Add(player);

                foreach (var list in _loadPlayerChunkLists) list.Clear();

                for (var i = 0; i < _loadPlayersScratch.Count; i++)
                {
                    if (i >= _loadPlayerChunkLists.Count) _loadPlayerChunkLists.Add(new List<Vector3i>());
                    var playerChunksToLoad = _loadPlayerChunkLists[i];
                    var playerChunk = ChunkInWorld(_loadPlayersScratch[i].Position.ToVector3i());

                    _loadDedup.Clear();

                    //Load the full BedrockY..WorldTop vertical band within TerrainRadius around the player.
                    for (var x = -TerrainRadius; x <= TerrainRadius; x++)
                        for (var z = -TerrainRadius; z <= TerrainRadius; z++)
                            for (var y = _generator.MinChunkY; y <= _generator.MaxChunkY; y++)
                            {
                                var chunkPos = new Vector3i(playerChunk.X + x, y, playerChunk.Z + z);
                                if (!_loadDedup.Add(chunkPos)) continue;

                                var known = _populatedChunks.ContainsKey(chunkPos);
                                if (!known)
                                    lock (_chunksReadyToAdd) known = _chunksReadyToAdd.ContainsKey(chunkPos);

                                if (known)
                                {
                                    //Reset chunk time so it will not be unloaded
                                    if (LoadedChunks.TryGetValue(chunkPos, out var chunk)) chunk.Time = DateTime.Now;
                                    continue;
                                }

                                playerChunksToLoad.Add(chunkPos);
                            }


                    _loadSortOrigin = playerChunk;
                    playerChunksToLoad.Sort(_loadSort);

                    if (playerChunksToLoad.Count > MaxChunksPerLoad)
                        playerChunksToLoad.RemoveRange(MaxChunksPerLoad, playerChunksToLoad.Count - MaxChunksPerLoad);
                }

                ExtensionHelper.ZipMerge(_loadPlayerChunkLists, _loadMerged);
                // Generate the batch in PARALLEL across cores — chunk gen is the streaming bottleneck (a
                // single thread was ~95% busy) and is embarrassingly parallel (the generator's per-thread
                // scratch makes Generate thread-safe; _populatedChunks is concurrent, disk load is lock-guarded,
                // staging is locked). Bounded so the mesh pool + main thread keep their cores.
                System.Threading.Tasks.Parallel.ForEach(_loadMerged, _genParallelOptions, chunkPos =>
                {
                    // Players with overlapping interest produce duplicate positions in the merged
                    // list; skip ones already queued or known so we don't load (or add) them twice.
                    if (_populatedChunks.ContainsKey(chunkPos)) return;
                    lock (_chunksReadyToAdd)
                        if (_chunksReadyToAdd.ContainsKey(chunkPos)) return;

                    ChunkTracer.Born(chunkPos);
                    var cachedChunk = LoadChunk(chunkPos);

                    if (cachedChunk.IsEmpty)
                    {
                        //Empty chunks dont need to be added to LoadedChunks
                        _populatedChunks[chunkPos] = 0;
                        ChunkTracer.Abandon(chunkPos);
                    }
                    else
                    {
                        lock (_chunksReadyToAdd)
                        {
                            _chunksReadyToAdd[chunkPos] = cachedChunk;
                            _stageQueueDepth = _chunksReadyToAdd.Count;
                        }

                        ChunkTracer.Staged(chunkPos);
                    }
                });

                Profiler.AddLoadAlloc(GC.GetAllocatedBytesForCurrentThread() - allocStart);
                // Only idle-sleep when nothing was loaded (world fully streamed around every player); under
                // load run flat out so streaming is gen-throughput-bound, not 16-chunks-per-10ms throttled.
                if (_loadMerged.Count == 0) Thread.Sleep(10);
            }
        }


        // Sole writer of _lodStore. Fills the ring [TerrainRadius .. LodRadius] with cheap surface-only LOD
        // columns around each player, evicts regions no player is near, and idles when caught up. Dormant
        // (no work) while LodRadius <= TerrainRadius (the default), so Phase-2 is provably zero-cost until
        // StateWorld raises LodRadius. Reads PlayerEntities (under lock) + the generator (pure); writes only
        // _lodStore — never LoadedChunks/light/dirty/staging (respects the per-container single-writer rule).
        private void LodThread()
        {
            while (!_unloaded)
            {
                var lodRadius = LodRadius;
                if (lodRadius <= TerrainRadius)   // dormant until the horizon is pushed past the real-chunk band
                {
                    Thread.Sleep(100);
                    continue;
                }

                _lodPlayersScratch.Clear();
                lock (PlayerEntities)
                    foreach (var player in PlayerEntities) _lodPlayersScratch.Add(player);
                if (_lodPlayersScratch.Count == 0) { Thread.Sleep(100); continue; }

                var lodBlocks = lodRadius * Chunk.Size;
                var innerBlocks = TerrainRadius * Chunk.Size - LodInnerOverlap;
                var lodBlocksSq = (float) lodBlocks * lodBlocks;
                var innerBlocksSq = (float) innerBlocks * innerBlocks;
                var regionReach = lodBlocks / LodColumn.RegionBlocks + 1;

                var filled = 0;
                for (var p = 0; p < _lodPlayersScratch.Count && filled < MaxLodRegionsPerIter; p++)
                {
                    var pb = _lodPlayersScratch[p].Position.ToVector3i();
                    var prX = pb.X >> 7;
                    var prZ = pb.Z >> 7;

                    for (var rx = -regionReach; rx <= regionReach && filled < MaxLodRegionsPerIter; rx++)
                    for (var rz = -regionReach; rz <= regionReach && filled < MaxLodRegionsPerIter; rz++)
                    {
                        var key = new Vector3i(prX + rx, 0, prZ + rz);
                        if (_lodStore.HasRegion(key)) continue;
                        if (!RegionInRing(key, pb, innerBlocksSq, lodBlocksSq)) continue;

                        FillLodRegion(key);
                        filled++;
                    }
                }

                EvictDistantLodRegions(lodBlocks + LodColumn.RegionBlocks);

                if (filled == 0) Thread.Sleep(20);
            }
        }

        private static bool RegionInRing(Vector3i key, Vector3i playerBlock, float innerSq, float outerSq)
        {
            var cx = (key.X << 7) + LodColumn.RegionBlocks / 2;
            var cz = (key.Z << 7) + LodColumn.RegionBlocks / 2;
            var dx = cx - playerBlock.X;
            var dz = cz - playerBlock.Z;
            var distSq = (float) dx * dx + (float) dz * dz;
            return distSq >= innerSq && distSq <= outerSq;
        }

        private void FillLodRegion(Vector3i key)
        {
            var columns = new long[LodColumn.ColumnCount];
            var baseX = key.X << 7;
            var baseZ = key.Z << 7;
            for (var cx = 0; cx < LodColumn.CellsPerAxis; cx++)
            for (var cz = 0; cz < LodColumn.CellsPerAxis; cz++)
                columns[cx * LodColumn.CellsPerAxis + cz] =
                    _generator.GetLodColumn(baseX + cx * LodColumn.Stride, baseZ + cz * LodColumn.Stride);
            _generator.DecorateLodRegion(key, columns);   // stamp tree canopies (matches real tree positions)
            _lodStore.PutRegion(new LodColumn(key, columns));
        }

        private void EvictDistantLodRegions(int dropBlocks)
        {
            _lodStore.SnapshotKeys(_lodKeysScratch);
            var dropSq = (float) dropBlocks * dropBlocks;
            for (var i = 0; i < _lodKeysScratch.Count; i++)
            {
                var key = _lodKeysScratch[i];
                var cx = (key.X << 7) + LodColumn.RegionBlocks / 2;
                var cz = (key.Z << 7) + LodColumn.RegionBlocks / 2;
                var keep = false;
                for (var p = 0; p < _lodPlayersScratch.Count; p++)
                {
                    var pb = _lodPlayersScratch[p].Position.ToVector3i();
                    var dx = cx - pb.X;
                    var dz = cz - pb.Z;
                    if ((float) dx * dx + (float) dz * dz <= dropSq) { keep = true; break; }
                }
                if (!keep) _lodStore.RemoveRegion(key);
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

            _lightChunkCache.Clear();

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
                        if (LightChunkEmpty(nextNode)) continue;

                        //Cache light level if not already cached
                        if (!cachedLightLevels.TryGetValue(nextNode, out var nextNodeLightLevel))
                        {
                            nextNodeLightLevel = GetBlockLightLevel(nextNode);
                            cachedLightLevels[nextNode] = nextNodeLightLevel;
                        }

                        //If the next nodes light level is higher or equal to our value it belongs to a
                        //stronger source: re-spread from its own level to refill the holes we open
                        if (nextNodeLightLevel[color] >= node.Value)
                        {
                            spreadQueue.Enqueue(new LightNode(nextNode, nextNodeLightLevel[color]));
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
                        if (LightChunkEmpty(nextNode)) continue;

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

        /// <summary>
        /// Sky-light flood for one edited cell, run right after <see cref="UpdateLightValues"/> on the same
        /// thread. Mirrors the block-light spread/remove BFS but the source is "open sky above"
        /// (<see cref="SkyExposed"/> ⇒ 15) rather than a block emitter, attenuation is a plain -1 in every
        /// direction (including down — deep open shafts dim with depth; see CLAUDE.md), and values are a
        /// single 0..15 scalar so it keeps its own int cache. Reuses the block-light queues + chunk cache.
        /// </summary>
        private void UpdateSkyValues(Vector3i blockPos)
        {
            _lightChunkCache.Clear();

            var occludingBlock = IsOpaqueFullBlock(blockPos);
            var oldSky = GetSkyLight(blockPos);

            var newSky = 0;
            if (!occludingBlock)
            {
                if (SkyExposed(blockPos)) newSky = LightLevel.SkyMax;
                foreach (var face in BlockFaceHelper.Faces)
                {
                    var neighbourSky = GetSkyLight(blockPos + face.GetNormali());
                    if (neighbourSky - 1 > newSky) newSky = neighbourSky - 1;
                }
            }

            var cachedSky = _skyLevelCache;
            cachedSky.Clear();
            cachedSky[blockPos] = newSky;

            var changed = _skyChanged;
            changed.Clear();
            if (newSky != oldSky) changed.Add(blockPos);

            var spreadQueue = _lightSpreadQueue;
            var removeQueue = _lightRemoveQueue;
            spreadQueue.Clear();
            removeQueue.Clear();

            if (newSky > oldSky)
                spreadQueue.Enqueue(new LightNode(blockPos, newSky));
            else if (newSky < oldSky)
                removeQueue.Enqueue(new LightNode(blockPos, oldSky));

            while (removeQueue.Count > 0)
            {
                var node = removeQueue.Dequeue();

                foreach (var face in BlockFaceHelper.Faces)
                {
                    var nextNode = node.Position + face.GetNormali();

                    if (LightChunkEmpty(nextNode)) continue;

                    if (!cachedSky.TryGetValue(nextNode, out var nextSky))
                    {
                        nextSky = GetSkyLight(nextNode);
                        cachedSky[nextNode] = nextSky;
                    }

                    //If the neighbour is at least as bright it belongs to a stronger source: re-spread
                    //from its own level to refill the holes we open (matches the block-light removal).
                    if (nextSky >= node.Value)
                    {
                        spreadQueue.Enqueue(new LightNode(nextNode, nextSky));
                        continue;
                    }
                    if (nextSky == 0) continue;
                    if (IsOpaqueFullBlock(nextNode)) continue;

                    cachedSky[nextNode] = 0;
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

                    if (LightChunkEmpty(nextNode)) continue;

                    if (!cachedSky.TryGetValue(nextNode, out var nextSky))
                    {
                        nextSky = GetSkyLight(nextNode);
                        cachedSky[nextNode] = nextSky;
                    }

                    var newValue = node.Value - 1;
                    if (nextSky >= newValue) continue;
                    if (IsOpaqueFullBlock(nextNode)) continue;

                    cachedSky[nextNode] = newValue;
                    changed.Add(nextNode);

                    if (newValue > 0)
                        spreadQueue.Enqueue(new LightNode(nextNode, newValue));
                }
            }

            foreach (var pos in changed)
                SetSkyLight(pos, cachedSky[pos]);
        }

        /// <summary>True if a straight upward scan from <paramref name="blockPos"/> reaches open sky (an
        /// unloaded chunk above the loaded column) without hitting an opaque full block. Bounded by
        /// <see cref="SkyScanMaxHeight"/>. Reuses the flood's <see cref="_lightChunkCache"/>.</summary>
        private bool SkyExposed(Vector3i blockPos)
        {
            var pos = blockPos;
            for (var i = 0; i < SkyScanMaxHeight; i++)
            {
                pos.Y++;
                if (LightChunkEmpty(pos)) return true;
                if (IsOpaqueFullBlock(pos)) return false;
            }

            return true;
        }

        private bool LightChunkEmpty(Vector3i blockPos)
        {
            var chunkPos = ChunkInWorld(blockPos);
            if (!_lightChunkCache.TryGetValue(chunkPos, out var chunk))
            {
                LoadedChunks.TryGetValue(chunkPos, out chunk);
                _lightChunkCache[chunkPos] = chunk;
            }

            return chunk == null;
        }

        private CachedChunk LoadChunk(Vector3i position)
        {
            var diskStart = Stopwatch.GetTimestamp();
            var chunk = _serializer.LoadChunk(this, position);
            Profiler.AddDiskTicks(Stopwatch.GetTimestamp() - diskStart);
            if (chunk != null)
            {
                Profiler.IncFromDisk();
                ChunkTracer.Loaded(position, ChunkSource.Disk);
                return chunk;
            }

            chunk = new CachedChunk(this, position);
            var genStart = Stopwatch.GetTimestamp();
            _generator.Generate(chunk, position);
            Profiler.AddGenTicks(Stopwatch.GetTimestamp() - genStart);
            Profiler.IncGenerated();
            ChunkTracer.Loaded(position, ChunkSource.Gen);
            return chunk;
        }
    }
}

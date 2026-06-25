using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.IO;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Networking
{
    /// <summary>
    /// The server's networking front-end: tracks client sessions, streams chunks by per-player
    /// interest, resends chunks the simulation marked dirty, and relays entity movement. Optionally
    /// listens for TCP clients; the integrated server registers a loopback endpoint directly.
    /// </summary>
    public class ServerNetwork
    {
        public const int DefaultPort = 25565;

        /// <summary>Block radius around a player within which loaded chunks are streamed to them. Default 160
        /// (10 chunks); in singleplayer StateWorld drives it from the render-distance slider, on a dedicated
        /// server it stays the default. An increase is picked up by the streamer automatically (the load
        /// thread loads more chunks → the loaded-count gate re-scans).</summary>
        private float _viewDistanceSq = 160f * 160f;

        public float ViewDistance
        {
            get => MathF.Sqrt(_viewDistanceSq);
            set => _viewDistanceSq = value * value;
        }

        /// <summary>Cap on new chunks sent per session per tick, so the initial flood streams in
        /// smoothly instead of stalling the tick by serializing every loaded chunk at once. Sized for the
        /// 20 tps tick (the loop used to run at the ~120 Hz display rate); ~6× the old per-frame cap keeps
        /// the same chunks/second streaming throughput.</summary>
        // Raised to keep up with the parallel LoadThread gen: streaming only fires on the 20 tps tick, so the
        // per-tick batch must be large or it caps throughput (192 → only 3840 chunks/s). Over loopback a chunk
        // is carried by reference (no serialize/GZip) so a big batch is cheap; the client pumps packets every
        // display frame (≫ 20 tps) and decodes off-thread. 512/tick × 20 tps ≈ 10 000 chunks/s.
        private const int MaxChunksPerTick = 512;

        // World-clock broadcast cadence (in ticks) — ~1 s at 20 tps; clients advance time locally between.
        private const int TimeSyncTicks = 20;
        private long _lastTimeSync = long.MinValue;

        // Resolved once from the generator (it spirals out for a land column, so cache it).
        private Vector3 _spawnPoint;
        private bool _spawnResolved;

        private readonly WorldServer _world;
        private readonly List<ClientSession> _sessions = new List<ClientSession>();
        private readonly ConcurrentQueue<IConnection> _pending = new ConcurrentQueue<IConnection>();

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running = true;

        // Reused across StreamChunks ticks (server tick thread only) so per-player interest scanning
        // allocates nothing steady-state.
        private readonly List<Vector3i> _newChunksScratch = new List<Vector3i>();

        // Phase-2 LOD streaming scratch (tick thread only). _lodRadiusSq defaults to ViewDistance so the ring
        // is empty (nothing to stream) until StateWorld raises it; the LOD store is also dormant by default.
        private float _lodRadiusSq = 160f * 160f;
        public float LodRadius
        {
            get => MathF.Sqrt(_lodRadiusSq);
            set => _lodRadiusSq = value * value;
        }
        private const int MaxLodRegionsPerTick = 8;
        private readonly List<Vector3i> _lodKeysScratch = new List<Vector3i>();
        private readonly List<Vector3i> _newLodScratch = new List<Vector3i>();
        private Vector3 _lodSortOrigin;

        // Reused per FlushBlockChanges tick: groups the drained per-block changes by chunk before
        // sending one BlockChanges packet per chunk per interested session.
        private readonly Dictionary<Vector3i, List<BlockChange>> _changesByChunk =
            new Dictionary<Vector3i, List<BlockChange>>();

        // Per-Pump timings + volumes, surfaced to the profiler (singleplayer: Pump runs on the main
        // thread, so a frame spike inside Pump shows up here split into chunk streaming vs delta flushing).
        public double LastStreamMs, LastFlushMs;
        public int LastChunksStreamed, LastChangesDrained, LastChangesPackets, LastLodStreamed;
        private readonly Stopwatch _pumpTimer = new Stopwatch();

        public ServerNetwork(WorldServer world)
        {
            _world = world;
        }

        public Vector3 SpawnPosition
        {
            get
            {
                if (!_spawnResolved)
                {
                    _spawnPoint = _world.SpawnPosition;
                    _spawnResolved = true;
                }

                return _spawnPoint;
            }
        }

        /// <summary>Registers an already-connected endpoint (e.g. the integrated loopback server side).</summary>
        public void AddConnection(IConnection connection) => _pending.Enqueue(connection);

        public void Listen(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            _acceptThread = new Thread(AcceptLoop) {Name = "Accept Thread", IsBackground = true};
            _acceptThread.Start();

            Logger.Info($"Server listening on port {port}");
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    break;
                }

                _pending.Enqueue(new TcpConnection(client));
                Logger.Info("Client connected");
            }
        }

        public void Pump()
        {
            while (_pending.TryDequeue(out var connection))
                _sessions.Add(new ClientSession(connection));

            foreach (var session in _sessions)
            {
                while (session.Connection.TryReceive(out var packet))
                    HandlePacket(session, packet);
            }

            RemoveDisconnected();

            _pumpTimer.Restart();
            StreamChunks();
            LastStreamMs = _pumpTimer.Elapsed.TotalMilliseconds;

            // Backfill the distant LOD ring AFTER the detail chunks so it never starves gameplay streaming.
            StreamLodRegions();

            SendReadySignals();
            SendTimeSync();
            SyncEntities();

            _pumpTimer.Restart();
            FlushBlockChanges();
            LastFlushMs = _pumpTimer.Elapsed.TotalMilliseconds;

            ResendDirtyChunks();
        }

        // Periodically broadcast the authoritative world clock so clients' day/night cycles stay in sync
        // (they advance it locally between packets). Once a second is plenty for a 4-minute day.
        private void SendTimeSync()
        {
            if (_world.TickCount - _lastTimeSync < TimeSyncTicks) return;
            _lastTimeSync = _world.TickCount;
            Broadcast(new WorldTimePacket {WorldSeconds = _world.WorldTimeSeconds}, null);
        }

        // Pickup reach (squared): a player within this distance of a pickup-ready dropped item collects it.
        private const float PickupRangeSq = 1.5f * 1.5f;

        /// <summary>Announces world-entity spawns/despawns the simulation produced, lets players collect nearby
        /// dropped items, and relays every live world entity's position to all clients each tick.</summary>
        private void SyncEntities()
        {
            while (_world.PendingSpawns.Count > 0)
                Broadcast(SpawnPacketFor(_world.PendingSpawns.Dequeue()), null);

            while (_world.PendingDespawns.Count > 0)
                Broadcast(new EntityDespawnPacket {EntityId = _world.PendingDespawns.Dequeue()}, null);

            CollectItems();

            foreach (var entity in _world.Entities)
            {
                if (entity.Dead) continue;
                Broadcast(new EntityMovePacket
                {
                    EntityId = entity.EntityId,
                    Position = entity.Position,
                    Pitch = entity.Pitch,
                    Yaw = entity.Yaw
                }, null);
            }
        }

        /// <summary>Transfers pickup-ready dropped items into the inventory of any player standing on them, then
        /// flags the emptied item entity dead (the world despawns it next tick).</summary>
        private void CollectItems()
        {
            foreach (var entity in _world.Entities)
            {
                if (!(entity is EntityItem item) || item.Dead || !item.CanPickup) continue;

                foreach (var session in _sessions)
                {
                    if (!session.LoggedIn) continue;
                    if ((session.Player.Position - item.Position).LengthSquared > PickupRangeSq) continue;

                    var stack = item.Stack;
                    session.Inventory.Add(ref stack);
                    item.Stack = stack;
                    session.Connection.Send(new InventoryStatePacket {Inventory = session.Inventory});
                    if (stack.IsEmpty) { item.Dead = true; break; }
                }
            }
        }

        private static EntitySpawnPacket SpawnPacketFor(Entity entity) => new EntitySpawnPacket
        {
            EntityId = entity.EntityId,
            TypeId = entity.Type?.Id ?? EntityType.PlayerTypeId,
            Stack = entity is EntityItem item ? item.Stack : ItemStack.Empty,
            Position = entity.Position,
            Pitch = entity.Pitch,
            Yaw = entity.Yaw
        };

        // Once the spawn column (the spawn chunk and the one below it, which the player stands on) has
        // been streamed to a session, tell that client it may finish joining. Authoritative and
        // transport-agnostic: the same packet drives the loading screen over loopback and TCP.
        private void SendReadySignals()
        {
            var spawnChunk = WorldBase.ChunkInWorld(SpawnPosition.ToVector3i());
            var belowChunk = spawnChunk - new Vector3i(0, 1, 0);

            foreach (var session in _sessions)
            {
                if (!session.LoggedIn || session.ReadySent) continue;
                if (!session.SentChunks.Contains(spawnChunk) || !session.SentChunks.Contains(belowChunk)) continue;

                session.Connection.Send(new PlayerReadyPacket());
                session.ReadySent = true;
            }
        }

        private void HandlePacket(ClientSession session, Packet packet)
        {
            switch (packet)
            {
                case LoginPacket login:
                    Login(session, login.Name);
                    break;
                case InventoryActionPacket action when session.LoggedIn:
                    if (action.SlotIndex >= 0 && action.SlotIndex < Inventory.Size)
                        session.Inventory.Slots[action.SlotIndex] = action.Stack;
                    break;
                case HeldSlotPacket held when session.LoggedIn:
                    session.Inventory.SelectedHotbar =
                        Math.Clamp(held.SelectedHotbar, 0, Inventory.HotbarSize - 1);
                    break;
                case EntityMovePacket move when session.LoggedIn:
                    session.Player.Position = move.Position;
                    session.Player.Pitch = move.Pitch;
                    session.Player.Yaw = move.Yaw;
                    Broadcast(new EntityMovePacket
                    {
                        EntityId = session.EntityId,
                        Position = move.Position,
                        Pitch = move.Pitch,
                        Yaw = move.Yaw
                    }, session);
                    break;
                case PlaceBlockRequestPacket place when session.LoggedIn:
                    ApplyPlaceRequest(session, place);
                    break;
                case ChunkReleasePacket release when session.LoggedIn:
                    session.SentChunks.Remove(release.Position);
                    break;
            }
        }

        private void Login(ClientSession session, string name)
        {
            if (session.LoggedIn) return;

            session.EntityId = _world.NextEntityId();
            session.PlayerName = name ?? "";
            session.Player = new EntityPlayer {Position = SpawnPosition, EntityId = session.EntityId};
            session.LoggedIn = true;
            _world.AddPlayer(session.Player);

            if (!PlayerSerializer.Load(_world.WorldDir, session.PlayerName, session.Inventory))
                SeedCreativeInventory(session.Inventory);

            session.Connection.Send(new LoginAcceptPacket {EntityId = session.EntityId, Spawn = SpawnPosition});
            session.Connection.Send(new WorldTimePacket {WorldSeconds = _world.WorldTimeSeconds});
            session.Connection.Send(new InventoryStatePacket {Inventory = session.Inventory});

            //Tell the new client about everyone already present, and everyone else about the newcomer.
            foreach (var other in _sessions)
            {
                if (other == session || !other.LoggedIn) continue;

                session.Connection.Send(new EntitySpawnPacket
                {
                    EntityId = other.EntityId,
                    Position = other.Player.Position,
                    Pitch = other.Player.Pitch,
                    Yaw = other.Player.Yaw
                });
            }

            // Tell the new client about every world entity (mobs/animals/dropped items) already alive.
            foreach (var entity in _world.Entities)
                session.Connection.Send(SpawnPacketFor(entity));

            Broadcast(new EntitySpawnPacket
            {
                EntityId = session.EntityId,
                Position = session.Player.Position,
                Pitch = session.Player.Pitch,
                Yaw = session.Player.Yaw
            }, session);

            Logger.Info($"Player {session.EntityId} logged in");
        }

        /// <summary>Fresh players get the first few placeable block items on the hotbar so the game is playable
        /// before opening the creative menu.</summary>
        private static void SeedCreativeInventory(Inventory inventory)
        {
            var slot = 0;
            foreach (var item in GameRegistry.Items)
            {
                if (slot >= Inventory.HotbarSize) break;
                if (item.GetBlock() == null) continue;
                inventory.Slots[slot++] = new ItemStack(item.Id, ItemStack.MaxStackSize);
            }
        }

        private void ApplyPlaceRequest(ClientSession session, PlaceBlockRequestPacket place)
        {
            var block = GameRegistry.GetBlock(place.BlockId);
            if (block.Id == 0)
            {
                // Breaking: drop the removed block as a collectible item (air/already-empty drops nothing).
                var broken = _world.GetBlock(place.Position);
                if (broken.Id != 0)
                    _world.DropItem(new ItemStack(broken.Id, 1), place.Position.ToVector3() + new Vector3(0.5f, 0.25f, 0.5f));
                _world.SetBlock(place.Position, BlockRegistry.BlockAir);
            }
            else
                _world.PlaceBlock(session.Player, place.Position, block, place.Metadata);
        }

        private void RemoveDisconnected()
        {
            for (var i = _sessions.Count - 1; i >= 0; i--)
            {
                var session = _sessions[i];
                if (session.Connection.IsConnected) continue;

                if (session.LoggedIn)
                {
                    PlayerSerializer.Save(_world.WorldDir, session.PlayerName, session.Inventory);
                    _world.RemovePlayer(session.Player);
                    Broadcast(new EntityDespawnPacket {EntityId = session.EntityId}, session);
                    Logger.Info($"Player {session.EntityId} disconnected");
                }

                _sessions.RemoveAt(i);
            }
        }

        private void StreamChunks()
        {
            var loadedCount = _world.LoadedChunks.Count;
            LastChunksStreamed = 0;

            foreach (var session in _sessions)
            {
                if (!session.LoggedIn) continue;

                var playerPos = session.Player.Position;

                // Skip the O(loaded) interest scan when nothing relevant changed since the last fully-drained
                // scan — the player hasn't crossed into a new chunk and no chunk has been (un)loaded. This is
                // the steady state while standing still in an already-streamed area, where the scan was the
                // dominant CPU cost.
                var playerChunk = WorldBase.ChunkInWorld(playerPos.ToVector3i());
                if (playerChunk == session.StreamScanChunk && loadedCount == session.StreamScanLoadedCount)
                    continue;

                _newChunksScratch.Clear();
                foreach (var entry in _world.LoadedChunks)
                {
                    var center = (entry.Key * Chunk.Size + new Vector3i(Chunk.Size / 2)).ToVector3();
                    if ((center - playerPos).LengthSquared > _viewDistanceSq) continue;
                    if (!session.SentChunks.Contains(entry.Key)) _newChunksScratch.Add(entry.Key);
                }

                //Send nearest chunks first so the world fills in around the player.
                _newChunksScratch.Sort((a, b) =>
                    ((a * Chunk.Size).ToVector3() - playerPos).LengthSquared.CompareTo(
                        ((b * Chunk.Size).ToVector3() - playerPos).LengthSquared));

                var sent = 0;
                foreach (var pos in _newChunksScratch)
                {
                    if (sent >= MaxChunksPerTick) break;
                    if (!_world.LoadedChunks.TryGetValue(pos, out var chunk)) continue;

                    session.Connection.Send(ChunkDataPacket.From(chunk));
                    ChunkTracer.Streamed(pos);
                    session.SentChunks.Add(pos);
                    sent++;
                }

                LastChunksStreamed += sent;

                // Only mark this scan "clean" once the in-range backlog is fully drained; if it was capped at
                // MaxChunksPerTick there is more to send, so leave the gate dirty to force a rescan next tick.
                if (_newChunksScratch.Count <= MaxChunksPerTick)
                {
                    session.StreamScanChunk = playerChunk;
                    session.StreamScanLoadedCount = loadedCount;
                }

                // The server never tells a client to drop a chunk: the client caches what it receives
                // and owns its own eviction, sending a ChunkRelease to clear the SentChunks entry. A
                // sent chunk therefore stays in SentChunks (so it is never resent) until either it is
                // dirtied (ResendDirtyChunks) or the client releases it.
            }
        }

        /// <summary>Streams Phase-2 LOD regions (the distant horizon) nearest-first to each session, capped
        /// per tick, gated on (player chunk, LOD-store region count) like <see cref="StreamChunks"/> so a
        /// stationary player does no per-tick region scan. Prunes per-session entries that fell out of range
        /// (so a returning player re-streams) — there is no client-side LOD release packet. Dormant by default
        /// (empty store + LodRadius == ViewDistance ⇒ nothing to send).</summary>
        private void StreamLodRegions()
        {
            var lodStore = _world.LodStore;
            var regionCount = lodStore.RegionCount;
            LastLodStreamed = 0;

            foreach (var session in _sessions)
            {
                if (!session.LoggedIn) continue;
                var playerPos = session.Player.Position;
                var playerChunk = WorldBase.ChunkInWorld(playerPos.ToVector3i());
                if (playerChunk == session.LodScanChunk && regionCount == session.LodScanRegionCount)
                    continue;

                lodStore.SnapshotKeys(_lodKeysScratch);
                _newLodScratch.Clear();
                for (var i = 0; i < _lodKeysScratch.Count; i++)
                {
                    var key = _lodKeysScratch[i];
                    if (LodRegionDistSq(key, playerPos) > _lodRadiusSq) { session.SentLodRegions.Remove(key); continue; }
                    if (!session.SentLodRegions.Contains(key)) _newLodScratch.Add(key);
                }

                _lodSortOrigin = playerPos;
                _newLodScratch.Sort(LodNearestFirst);

                var sent = 0;
                foreach (var key in _newLodScratch)
                {
                    if (sent >= MaxLodRegionsPerTick) break;
                    if (!lodStore.TryGetRegion(key, out var region)) continue;
                    session.Connection.Send(LodColumnDataPacket.From(region));
                    session.SentLodRegions.Add(key);
                    sent++;
                }
                LastLodStreamed += sent;

                if (_newLodScratch.Count <= MaxLodRegionsPerTick)
                {
                    session.LodScanChunk = playerChunk;
                    session.LodScanRegionCount = regionCount;
                }
            }
        }

        private static float LodRegionDistSq(Vector3i key, Vector3 playerPos)
        {
            var dx = (key.X << 7) + LodColumn.RegionBlocks / 2 - playerPos.X;
            var dz = (key.Z << 7) + LodColumn.RegionBlocks / 2 - playerPos.Z;
            return dx * dx + dz * dz;
        }

        private int LodNearestFirst(Vector3i a, Vector3i b)
            => LodRegionDistSq(a, _lodSortOrigin).CompareTo(LodRegionDistSq(b, _lodSortOrigin));

        /// <summary>Drains the server's per-block change buffer and sends one compact BlockChanges
        /// packet per chunk to every session holding that chunk. Edits and light propagation flow
        /// through here; whole-chunk resends are reserved for block-data changes (see below) and
        /// initial streaming.</summary>
        private void FlushBlockChanges()
        {
            LastChangesDrained = 0;
            LastChangesPackets = 0;
            if (_world.BlockChanges.IsEmpty) return;

            _changesByChunk.Clear();
            // Enumerating + TryRemove drains the snapshot of entries present now; changes the worker
            // threads add during the drain stay for the next tick (enumeration terminates, so a busy
            // light thread can't trap this loop). Last-write-wins dedup already happened at enqueue.
            foreach (var kvp in _world.BlockChanges)
            {
                if (!_world.BlockChanges.TryRemove(kvp.Key, out var change)) continue;

                if (!_changesByChunk.TryGetValue(change.ChunkPos, out var list))
                {
                    list = new List<BlockChange>();
                    _changesByChunk[change.ChunkPos] = list;
                }

                list.Add(change);
                LastChangesDrained++;
            }

            foreach (var entry in _changesByChunk)
            {
                BlockChangesPacket packet = null;
                foreach (var session in _sessions)
                {
                    if (!session.LoggedIn || !session.SentChunks.Contains(entry.Key)) continue;
                    if (packet == null) packet = new BlockChangesPacket {ChunkPos = entry.Key, Changes = entry.Value};
                    session.Connection.Send(packet);
                    LastChangesPackets++;
                }
            }
        }

        private void ResendDirtyChunks()
        {
            if (_world.DirtyChunks.IsEmpty) return;

            var dirty = _world.DirtyChunks.Keys.ToList();
            foreach (var pos in dirty)
            {
                _world.DirtyChunks.TryRemove(pos, out _);

                if (!_world.LoadedChunks.TryGetValue(pos, out var chunk)) continue;

                foreach (var session in _sessions)
                {
                    if (!session.SentChunks.Contains(pos)) continue;
                    session.Connection.Send(ChunkDataPacket.From(chunk));
                }
            }
        }

        private void Broadcast(Packet packet, ClientSession except)
        {
            foreach (var session in _sessions)
            {
                if (session == except || !session.LoggedIn) continue;
                session.Connection.Send(packet);
            }
        }

        public void Stop()
        {
            _running = false;

            // Persist inventories for players still connected at shutdown (SP quit / dedicated stop) — they
            // never sent a disconnect, so RemoveDisconnected wouldn't have saved them.
            foreach (var session in _sessions)
                if (session.LoggedIn)
                    PlayerSerializer.Save(_world.WorldDir, session.PlayerName, session.Inventory);

            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // listener already stopped
            }
        }
    }
}

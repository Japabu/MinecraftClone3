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
        private const int MaxChunksPerTick = 48;

        // Resolved once from the generator (it spirals out for a land column, so cache it).
        private Vector3 _spawnPoint;
        private bool _spawnResolved;

        private readonly WorldServer _world;
        private readonly List<ClientSession> _sessions = new List<ClientSession>();
        private readonly ConcurrentQueue<IConnection> _pending = new ConcurrentQueue<IConnection>();

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running = true;

        private int _nextEntityId = 1;

        // Reused across StreamChunks ticks (server tick thread only) so per-player interest scanning
        // allocates nothing steady-state.
        private readonly List<Vector3i> _newChunksScratch = new List<Vector3i>();

        // Reused per FlushBlockChanges tick: groups the drained per-block changes by chunk before
        // sending one BlockChanges packet per chunk per interested session.
        private readonly Dictionary<Vector3i, List<BlockChange>> _changesByChunk =
            new Dictionary<Vector3i, List<BlockChange>>();

        // Per-Pump timings + volumes, surfaced to the profiler (singleplayer: Pump runs on the main
        // thread, so a frame spike inside Pump shows up here split into chunk streaming vs delta flushing).
        public double LastStreamMs, LastFlushMs;
        public int LastChunksStreamed, LastChangesDrained, LastChangesPackets;
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

            SendReadySignals();

            _pumpTimer.Restart();
            FlushBlockChanges();
            LastFlushMs = _pumpTimer.Elapsed.TotalMilliseconds;

            ResendDirtyChunks();
        }

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
                case LoginPacket _:
                    Login(session);
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

        private void Login(ClientSession session)
        {
            if (session.LoggedIn) return;

            session.EntityId = _nextEntityId++;
            session.Player = new EntityPlayer {Position = SpawnPosition};
            session.LoggedIn = true;
            _world.AddPlayer(session.Player);

            session.Connection.Send(new LoginAcceptPacket {EntityId = session.EntityId, Spawn = SpawnPosition});

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

            Broadcast(new EntitySpawnPacket
            {
                EntityId = session.EntityId,
                Position = session.Player.Position,
                Pitch = session.Player.Pitch,
                Yaw = session.Player.Yaw
            }, session);

            Logger.Info($"Player {session.EntityId} logged in");
        }

        private void ApplyPlaceRequest(ClientSession session, PlaceBlockRequestPacket place)
        {
            var block = GameRegistry.GetBlock(place.BlockId);
            if (block.Id == 0)
                _world.SetBlock(place.Position, BlockRegistry.BlockAir);
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

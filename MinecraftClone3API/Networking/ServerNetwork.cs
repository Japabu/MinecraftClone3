using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        /// <summary>Block radius around a player within which loaded chunks are streamed to them.</summary>
        private const float ViewDistance = 256f;
        private const float ViewDistanceSq = ViewDistance * ViewDistance;

        /// <summary>Cap on new chunks sent per session per tick, so the initial flood streams in
        /// smoothly instead of stalling the tick by serializing every loaded chunk at once.</summary>
        private const int MaxChunksPerTick = 8;

        private static readonly Vector3 Spawn = new Vector3(0, 12, 0);
        private static readonly Vector3i TorchPos = new Vector3i(0, 10, 0);

        private readonly WorldServer _world;
        private readonly List<ClientSession> _sessions = new List<ClientSession>();
        private readonly ConcurrentQueue<IConnection> _pending = new ConcurrentQueue<IConnection>();

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running = true;

        private int _nextEntityId = 1;
        private bool _torchPlaced;

        // Reused across StreamChunks ticks (server tick thread only) so per-player interest scanning
        // allocates nothing steady-state.
        private readonly List<Vector3i> _newChunksScratch = new List<Vector3i>();

        public ServerNetwork(WorldServer world)
        {
            _world = world;
        }

        public Vector3 SpawnPosition => Spawn;

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
            PlaceTorch();
            StreamChunks();
            ResendDirtyChunks();
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
            session.Player = new EntityPlayer {Position = Spawn};
            session.LoggedIn = true;
            _world.AddPlayer(session.Player);

            session.Connection.Send(new LoginAcceptPacket {EntityId = session.EntityId, Spawn = Spawn});

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
                _world.PlaceBlock(session.Player, place.Position, block);
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

        private void PlaceTorch()
        {
            if (_torchPlaced || _world.IsBlockInEmptyChunk(TorchPos)) return;
            _world.SetBlock(TorchPos, GameRegistry.GetBlock("Vanilla:Torch"));
            _torchPlaced = true;
        }

        private void StreamChunks()
        {
            foreach (var session in _sessions)
            {
                if (!session.LoggedIn) continue;

                var playerPos = session.Player.Position;

                _newChunksScratch.Clear();
                foreach (var entry in _world.LoadedChunks)
                {
                    var center = (entry.Key * Chunk.Size + new Vector3i(Chunk.Size / 2)).ToVector3();
                    if ((center - playerPos).LengthSquared > ViewDistanceSq) continue;
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
                    session.SentChunks.Add(pos);
                    sent++;
                }

                // The server never tells a client to drop a chunk: the client caches what it receives
                // and owns its own eviction, sending a ChunkRelease to clear the SentChunks entry. A
                // sent chunk therefore stays in SentChunks (so it is never resent) until either it is
                // dirtied (ResendDirtyChunks) or the client releases it.
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

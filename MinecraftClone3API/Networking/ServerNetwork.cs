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
using MinecraftClone3API.WorldGen;
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

        // Persist connected players this often (≈30 s at 20 ticks/s) so a crash loses at most one interval;
        // disconnect and shutdown still save immediately.
        private const int PlayerSaveTicks = 600;
        private long _lastPlayerSave = long.MinValue;

        // Resolved once from the generator (it spirals out for a land column, so cache it).
        private Vector3 _spawnPoint;
        private bool _spawnResolved;

        // The primary (Overworld) world plus every sibling dimension spun up on demand (the Nether the first
        // time a player steps through a portal). Each is its own WorldServer over its own directory; the tick
        // thread is the sole mutator of _worlds (GetOrCreateWorld during Pump), so the snapshot list it ticks is
        // race-free. Keyed by dimension key.
        private readonly WorldServer _world;
        private readonly Dictionary<string, WorldServer> _worlds = new Dictionary<string, WorldServer>();
        private readonly List<WorldServer> _worldList = new List<WorldServer>();
        private readonly long _seed;
        private readonly string _worldDir;

        private readonly List<ClientSession> _sessions = new List<ClientSession>();
        private readonly ConcurrentQueue<IConnection> _pending = new ConcurrentQueue<IConnection>();

        // Ticks a player must stand in a portal before transferring (matches the brief MC "soak" so brushing a
        // portal doesn't fling you instantly).
        private const int PortalSoakTicks = 16;

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
            _seed = world.Seed;
            _worldDir = world.WorldDir;
            _worlds[world.DimensionKey] = world;
            _worldList.Add(world);
        }

        /// <summary>The world for a dimension key, creating (and starting) its <see cref="WorldServer"/> the
        /// first time it's needed. Sibling dimensions live in a <c>DIM_*</c> subfolder of the primary world dir;
        /// they inherit the primary's runtime view radius. Tick-thread only.</summary>
        private WorldServer GetOrCreateWorld(string dimensionKey)
        {
            if (_worlds.TryGetValue(dimensionKey, out var existing)) return existing;

            var dir = System.IO.Path.Combine(_worldDir, "DIM_" + dimensionKey.Replace(':', '_'));
            var world = new WorldServer(_seed, dir, dimensionKey) {TerrainRadius = _world.TerrainRadius};
            _worlds[dimensionKey] = world;
            _worldList.Add(world);
            Logger.Info($"Dimension \"{dimensionKey}\" started");
            return world;
        }

        /// <summary>Ticks every live dimension world once (replaces a single <c>WorldServer.Update()</c>).
        /// Called by the host each server tick before <see cref="Pump"/>.</summary>
        public void TickWorlds()
        {
            for (var i = 0; i < _worldList.Count; i++) _worldList[i].Update();
        }

        /// <summary>Saves + stops every dimension world on shutdown.</summary>
        public void UnloadWorlds()
        {
            for (var i = 0; i < _worldList.Count; i++) _worldList[i].Unload();
        }

        /// <summary>Sends a packet to every logged-in session currently in <paramref name="world"/> (optionally
        /// skipping one) — the dimension-scoped form of <see cref="Broadcast"/>.</summary>
        private void BroadcastTo(WorldServer world, Packet packet, ClientSession except)
        {
            foreach (var session in _sessions)
            {
                if (session == except || !session.LoggedIn || session.World != world) continue;
                session.Connection.Send(packet);
            }
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

            UpdatePortals();
            ProcessPendingTransfers();

            _pumpTimer.Restart();
            StreamChunks();
            LastStreamMs = _pumpTimer.Elapsed.TotalMilliseconds;

            // Backfill the distant LOD ring AFTER the detail chunks so it never starves gameplay streaming.
            StreamLodRegions();

            SendReadySignals();
            SendTimeSync();
            SyncEntities();
            SyncContainers();
            SyncPlayerStats();
            SavePlayersPeriodic();

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
            foreach (var session in _sessions)
                if (session.LoggedIn)
                    session.Connection.Send(new WorldTimePacket {WorldSeconds = session.World.WorldTimeSeconds});
        }

        // Periodically persist connected players so a crash loses at most PlayerSaveTicks of progress. Player
        // files live under the primary world dir (mirrors the disconnect/shutdown saves), regardless of which
        // dimension the player is currently in.
        private void SavePlayersPeriodic()
        {
            if (_world.TickCount - _lastPlayerSave < PlayerSaveTicks) return;
            _lastPlayerSave = _world.TickCount;
            foreach (var session in _sessions)
                if (session.LoggedIn)
                    PlayerSerializer.Save(_world.WorldDir, session.PlayerName, session.Inventory, session.Player,
                        session.World.DimensionKey);
        }

        // Pickup reach (squared): a player within this distance of a pickup-ready dropped item collects it.
        private const float PickupRangeSq = 1.5f * 1.5f;

        /// <summary>Announces world-entity spawns/despawns the simulation produced, lets players collect nearby
        /// dropped items, and relays every live world entity's position to all clients each tick.</summary>
        private void SyncEntities()
        {
            foreach (var world in _worldList)
            {
                while (world.PendingSpawns.Count > 0)
                    BroadcastTo(world, SpawnPacketFor(world.PendingSpawns.Dequeue()), null);

                while (world.PendingDespawns.Count > 0)
                    BroadcastTo(world, new EntityDespawnPacket {EntityId = world.PendingDespawns.Dequeue()}, null);
            }

            while (_world.PendingTeleports.Count > 0)
                TeleportPlayer(_world.PendingTeleports.Dequeue());

            CollectItems();

            foreach (var world in _worldList)
            foreach (var entity in world.Entities)
            {
                if (entity.Dead) continue;
                BroadcastTo(world, new EntityMovePacket
                {
                    EntityId = entity.EntityId,
                    Position = entity.Position,
                    Pitch = entity.Pitch,
                    Yaw = entity.Yaw
                }, null);
            }
        }

        /// <summary>Carries out a landed ender pearl's teleport: commands the owning client to snap there (the
        /// player is position-authoritative), mirrors it on the server copy, and applies Minecraft's 5 points of
        /// pearl fall damage in survival (the existing death path handles Health ≤ 0).</summary>
        private void TeleportPlayer(WorldServer.Teleport tp)
        {
            foreach (var session in _sessions)
            {
                if (!session.LoggedIn || session.EntityId != tp.OwnerId) continue;

                session.Connection.Send(new PlayerTeleportPacket {Position = tp.Position});
                session.Player.Position = tp.Position;
                session.Player.LastTickPosition = tp.Position;
                if (session.Player.GameMode != GameMode.Creative)
                    session.Player.Health = Math.Max(0f, session.Player.Health - 5f);
                return;
            }
        }

        /// <summary>Transfers pickup-ready dropped items into the inventory of any player standing on them, then
        /// flags the emptied item entity dead (the world despawns it next tick).</summary>
        private void CollectItems()
        {
            foreach (var world in _worldList)
            foreach (var entity in world.Entities)
            {
                if (!(entity is EntityItem item) || item.Dead || !item.CanPickup) continue;

                foreach (var session in _sessions)
                {
                    if (!session.LoggedIn || session.World != world) continue;
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
            Yaw = entity.Yaw,
            Data = entity.Data
        };

        // Once the spawn column (the spawn chunk and the one below it, which the player stands on) has
        // been streamed to a session, tell that client it may finish joining. Authoritative and
        // transport-agnostic: the same packet drives the loading screen over loopback and TCP.
        private void SendReadySignals()
        {
            foreach (var session in _sessions)
            {
                if (!session.LoggedIn || session.ReadySent || !session.ReadyGate.HasValue) continue;

                var spawnChunk = WorldBase.ChunkInWorld(session.ReadyGate.Value.ToVector3i());
                var belowChunk = spawnChunk - new Vector3i(0, 1, 0);
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
                    if (action.SlotIndex >= Inventory.ArmorActionBase)
                    {
                        var armorIdx = action.SlotIndex - Inventory.ArmorActionBase;
                        if (armorIdx < Inventory.ArmorSize) session.Inventory.Armor[armorIdx] = action.Stack;
                    }
                    else if (action.SlotIndex >= 0 && action.SlotIndex < Inventory.Size)
                        session.Inventory.Slots[action.SlotIndex] = action.Stack;
                    break;
                case HeldSlotPacket held when session.LoggedIn:
                    session.Inventory.SelectedHotbar =
                        Math.Clamp(held.SelectedHotbar, 0, Inventory.HotbarSize - 1);
                    break;
                case EntityMovePacket move when session.LoggedIn && session.PendingPortalWorld == null:
                    session.Player.Position = move.Position;
                    session.Player.Pitch = move.Pitch;
                    session.Player.Yaw = move.Yaw;
                    BroadcastTo(session.World, new EntityMovePacket
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
                case UseItemRequestPacket use when session.LoggedIn:
                    ApplyUseRequest(session, use);
                    break;
                case UseItemOnEntityRequestPacket useOn when session.LoggedIn:
                    ApplyUseOnEntityRequest(session, useOn);
                    break;
                case AttackEntityRequestPacket attack when session.LoggedIn:
                    ApplyAttackRequest(session, attack);
                    break;
                case PlayerFallPacket fall when session.LoggedIn:
                    PlayerSurvival.ApplyFallDamage(session.Player, fall.FallDistance);
                    break;
                case SetGameModeRequestPacket gm when session.LoggedIn:
                    SetGameMode(session, (GameMode) gm.GameMode);
                    break;
                case RespawnRequestPacket _ when session.LoggedIn:
                    Respawn(session);
                    break;
                case DropItemRequestPacket drop when session.LoggedIn:
                    ApplyDropRequest(session, drop);
                    break;
                case ChunkReleasePacket release when session.LoggedIn:
                    session.SentChunks.Remove(release.Position);
                    break;
                case OpenContainerPacket open when session.LoggedIn:
                    session.OpenContainer = open.Position;
                    SendContainerState(session, open.Position);
                    break;
                case CloseContainerPacket _ when session.LoggedIn:
                    session.OpenContainer = null;
                    break;
                case ContainerSlotPacket slot when session.LoggedIn:
                    if (session.World.GetBlockData(slot.Position) is ContainerBlockData container)
                    {
                        container.SetSlot(slot.Slot, slot.Stack);
                        session.World.TouchBlockDataForSave(slot.Position);
                    }
                    break;
            }
        }

        // Streams the live state of every open container block to the clients viewing it (called each Pump),
        // so furnace burn/cook progress and slot contents stay in sync on the screen.
        private void SyncContainers()
        {
            foreach (var session in _sessions)
                if (session.OpenContainer.HasValue)
                    SendContainerState(session, session.OpenContainer.Value);
        }

        private void SendContainerState(ClientSession session, Vector3i pos)
        {
            if (session.World.GetBlockData(pos) is ContainerBlockData container)
                session.Connection.Send(new ContainerStatePacket
                {
                    Position = pos,
                    Slots = container.Slots,
                    Fields = container.SyncFields
                });
        }

        /// <summary>Sends each player its own survival stats when a value changed since the last send (so the
        /// owning client's HUD/death screen stay in sync). Also latches <see cref="ClientSession.Dead"/> from
        /// the authoritative health.</summary>
        private void SyncPlayerStats()
        {
            foreach (var session in _sessions)
            {
                if (!session.LoggedIn) continue;
                var p = session.Player;
                session.Dead = p.Health <= 0f;

                if (session.StatsSent && p.Health == session.LastHealth && p.Hunger == session.LastHunger &&
                    p.Saturation == session.LastSaturation && (byte) p.GameMode == session.LastGameMode &&
                    session.Dead == session.LastDead)
                    continue;

                session.StatsSent = true;
                session.LastHealth = p.Health;
                session.LastHunger = p.Hunger;
                session.LastSaturation = p.Saturation;
                session.LastGameMode = (byte) p.GameMode;
                session.LastDead = session.Dead;

                session.Connection.Send(new PlayerStatsPacket
                {
                    Health = p.Health,
                    MaxHealth = PlayerSurvival.MaxHealth,
                    Hunger = p.Hunger,
                    Saturation = p.Saturation,
                    GameMode = (byte) p.GameMode,
                    Dead = session.Dead
                });
            }
        }

        private static void SetGameMode(ClientSession session, GameMode mode)
        {
            session.Player.GameMode = mode;
            // Survival forbids flight; the meaningful gate is client-side, but clear the flag for cleanliness.
            if (mode == GameMode.Survival) session.Player.Flying = false;
        }

        private void Respawn(ClientSession session)
        {
            if (!session.Dead) return;
            PlayerSurvival.Reset(session.Player);
            session.Dead = false;

            // Respawning always returns to the Overworld spawn. If the player died in another dimension, run a
            // full transfer (drop their world client-side and re-load the Overworld); otherwise just reposition.
            if (session.World != _world)
            {
                BeginRespawnTransfer(session);
            }
            else
            {
                session.Player.Position = SpawnPosition;
                session.Player.LastTickPosition = SpawnPosition;
            }
        }

        private void Login(ClientSession session, string name)
        {
            if (session.LoggedIn) return;

            session.EntityId = _world.NextEntityId();
            session.PlayerName = name ?? "";
            session.World = _world;
            session.Player = new EntityPlayer
            {
                Position = SpawnPosition, LastTickPosition = SpawnPosition, EntityId = session.EntityId,
                Inventory = session.Inventory
            };
            session.LoggedIn = true;
            _world.AddPlayer(session.Player);

            // Load restores the player's saved position too (or leaves it at spawn for a new player); the join
            // handshake pre-streams the column under wherever they land.
            if (!PlayerSerializer.Load(_world.WorldDir, session.PlayerName, session.Inventory, session.Player,
                    _world.DimensionKey))
                SeedCreativeInventory(session.Inventory);

            session.ReadyGate = session.Player.Position;
            session.Connection.Send(new LoginAcceptPacket {EntityId = session.EntityId, Spawn = session.Player.Position});
            session.Connection.Send(new WorldTimePacket {WorldSeconds = _world.WorldTimeSeconds});
            session.Connection.Send(new InventoryStatePacket {Inventory = session.Inventory});

            SendPresentEntities(session);

            BroadcastTo(session.World, new EntitySpawnPacket
            {
                EntityId = session.EntityId,
                Position = session.Player.Position,
                Pitch = session.Player.Pitch,
                Yaw = session.Player.Yaw
            }, session);

            Logger.Info($"Player {session.EntityId} logged in");
        }

        /// <summary>Tells <paramref name="session"/> about every other player and world entity currently in its
        /// dimension (used on login and after a portal transfer, once the client's world has been reset).</summary>
        private void SendPresentEntities(ClientSession session)
        {
            foreach (var other in _sessions)
            {
                if (other == session || !other.LoggedIn || other.World != session.World) continue;

                session.Connection.Send(new EntitySpawnPacket
                {
                    EntityId = other.EntityId,
                    Position = other.Player.Position,
                    Pitch = other.Player.Pitch,
                    Yaw = other.Player.Yaw
                });
            }

            foreach (var entity in session.World.Entities)
                session.Connection.Send(SpawnPacketFor(entity));
        }

        /// <summary>Fresh players get the spawn eggs followed by the first placeable block items on the hotbar
        /// so entities are testable and the game is playable before opening the creative menu.</summary>
        private static void SeedCreativeInventory(Inventory inventory)
        {
            var slot = 0;
            foreach (var item in GameRegistry.Items)
            {
                if (slot >= Inventory.HotbarSize) break;
                if (!item.IsUsable && !item.UsableOnEntity) continue;
                inventory.Slots[slot++] = new ItemStack(item.Id, item.MaxStackSize);
            }

            foreach (var item in GameRegistry.Items)
            {
                if (slot >= Inventory.HotbarSize) break;
                if (item.GetBlock() == null) continue;
                inventory.Slots[slot++] = new ItemStack(item.Id, item.MaxStackSize);
            }
        }

        private void ApplyPlaceRequest(ClientSession session, PlaceBlockRequestPacket place)
        {
            var world = session.World;
            var block = GameRegistry.GetBlock(place.BlockId);
            if (block.Id == 0)
            {
                // Breaking: drop the removed block's item form (air/already-empty drops nothing).
                var broken = world.GetBlock(place.Position);
                if (broken.Id != 0 && GameRegistry.TryGetItem(broken.RegistryKey, out var dropped))
                    world.DropItem(new ItemStack(dropped.Id, 1),
                        place.Position.ToVector3() + new Vector3(0.5f, 0.25f, 0.5f));
                world.SetBlock(place.Position, BlockRegistry.BlockAir);
            }
            else
                world.PlaceBlock(session.Player, place.Position, block, place.Metadata);
        }

        /// <summary>Runs the held item's server-side use (e.g. a spawn egg spawning its mob). The held item
        /// is read from the server's authoritative inventory copy, not the request, so it can't be spoofed.</summary>
        private void ApplyUseRequest(ClientSession session, UseItemRequestPacket use)
        {
            var slot = session.Inventory.SelectedHotbar;
            var held = session.Inventory.Slots[slot];
            var item = held.Item;
            if (item == null || !item.IsUsable) return;
            // Item-specific gate (e.g. food only applies in survival when there is hunger to refill).
            if (!item.CanUseServer(session.Player)) return;

            item.OnUseServer(session.World, session.Player, use.Position.ToVector3() + new Vector3(0.5f, 0f, 0.5f));

            if (item.ConsumesOnUse)
            {
                session.Inventory.Slots[slot] =
                    held.Count - 1 <= 0 ? ItemStack.Empty : held.WithCount(held.Count - 1);
                session.Connection.Send(new InventoryStatePacket {Inventory = session.Inventory});
            }
            else if (item.RefreshInventoryAfterUse)
                session.Connection.Send(new InventoryStatePacket {Inventory = session.Inventory});
        }

        /// <summary>Runs the held item's server-side action against the targeted entity (shears on a sheep). The
        /// target is resolved from the server's own entity list (not the request), and any resulting
        /// <see cref="EntityData"/> change is broadcast so every client's copy stays in step.</summary>
        private void ApplyUseOnEntityRequest(ClientSession session, UseItemOnEntityRequestPacket useOn)
        {
            var item = session.Inventory.SelectedItem.Item;
            if (item == null || !item.UsableOnEntity) return;
            var target = session.World.FindEntity(useOn.EntityId);
            if (target == null) return;

            item.OnUseOnEntity(session.World, session.Player, target);
            BroadcastTo(session.World, new EntityDataPacket {EntityId = target.EntityId, Data = target.Data}, null);
        }

        /// <summary>Applies a melee attack against a creature: the target is resolved from the server's own
        /// entity list (the request only names an id, so it can't act on an arbitrary one), and damage comes
        /// from the player's authoritative held item. Death (loot + despawn) is handled by
        /// <see cref="EntityCombat"/>; the resulting despawn already streams to clients.</summary>
        private void ApplyAttackRequest(ClientSession session, AttackEntityRequestPacket attack)
        {
            if (!(_world.FindEntity(attack.EntityId) is EntityCreature target)) return;

            var held = session.Inventory.SelectedItem.Item;
            var damage = held?.AttackDamage ?? EntityCombat.BaseHandDamage;
            EntityCombat.DamageEntity(_world, target, damage, session.Player.Position);
        }

        /// <summary>Drops the player's held hotbar item (one, or the whole stack on Ctrl+Q): decrements the
        /// authoritative inventory, spawns the drop in front of the player thrown along their look direction,
        /// and echoes the new inventory back so the client replica stays in step.</summary>
        private void ApplyDropRequest(ClientSession session, DropItemRequestPacket drop)
        {
            var slot = session.Inventory.SelectedHotbar;
            var held = session.Inventory.Slots[slot];
            if (held.IsEmpty) return;

            var count = drop.All ? held.Count : 1;
            session.Inventory.Slots[slot] =
                held.Count - count <= 0 ? ItemStack.Empty : held.WithCount(held.Count - count);

            var yaw = session.Player.Yaw;
            var pitch = session.Player.Pitch;
            var forward = new Vector3(
                (float) (Math.Sin(yaw) * Math.Cos(pitch)),
                (float) Math.Sin(pitch),
                (float) (Math.Cos(yaw) * Math.Cos(pitch)));

            var entity = session.World.DropItem(held.WithCount(count),
                session.Player.Position + new Vector3(0f, 1.4f, 0f) + forward * 0.3f);
            if (entity != null)
            {
                entity.Velocity = forward * 0.3f + new Vector3(0f, 0.1f, 0f);
                entity.PickupDelay = 40; // ~2 s so a thrown item doesn't fly straight back in
            }

            session.Connection.Send(new InventoryStatePacket {Inventory = session.Inventory});
        }

        private void RemoveDisconnected()
        {
            for (var i = _sessions.Count - 1; i >= 0; i--)
            {
                var session = _sessions[i];
                if (session.Connection.IsConnected) continue;

                if (session.LoggedIn)
                {
                    PlayerSerializer.Save(_world.WorldDir, session.PlayerName, session.Inventory, session.Player,
                        session.World.DimensionKey);
                    session.World.RemovePlayer(session.Player);
                    BroadcastTo(session.World, new EntityDespawnPacket {EntityId = session.EntityId}, session);
                    Logger.Info($"Player {session.EntityId} disconnected");
                }

                _sessions.RemoveAt(i);
            }
        }

        private void StreamChunks()
        {
            LastChunksStreamed = 0;

            foreach (var session in _sessions)
            {
                if (!session.LoggedIn) continue;

                var world = session.World;
                var loadedCount = world.LoadedChunks.Count;
                var playerPos = session.Player.Position;

                // Skip the O(loaded) interest scan when nothing relevant changed since the last fully-drained
                // scan — the player hasn't crossed into a new chunk and no chunk has been (un)loaded. This is
                // the steady state while standing still in an already-streamed area, where the scan was the
                // dominant CPU cost.
                var playerChunk = WorldBase.ChunkInWorld(playerPos.ToVector3i());
                if (playerChunk == session.StreamScanChunk && loadedCount == session.StreamScanLoadedCount)
                    continue;

                _newChunksScratch.Clear();
                foreach (var entry in world.LoadedChunks)
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
                    if (!world.LoadedChunks.TryGetValue(pos, out var chunk)) continue;

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
            LastLodStreamed = 0;

            foreach (var session in _sessions)
            {
                if (!session.LoggedIn) continue;
                var lodStore = session.World.LodStore;
                var regionCount = lodStore.RegionCount;
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

            foreach (var world in _worldList)
            {
                if (world.BlockChanges.IsEmpty) continue;

                _changesByChunk.Clear();
                // Enumerating + TryRemove drains the snapshot of entries present now; changes the worker
                // threads add during the drain stay for the next tick (enumeration terminates, so a busy
                // light thread can't trap this loop). Last-write-wins dedup already happened at enqueue.
                foreach (var kvp in world.BlockChanges)
                {
                    if (!world.BlockChanges.TryRemove(kvp.Key, out var change)) continue;

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
                        if (!session.LoggedIn || session.World != world || !session.SentChunks.Contains(entry.Key))
                            continue;
                        if (packet == null) packet = new BlockChangesPacket {ChunkPos = entry.Key, Changes = entry.Value};
                        session.Connection.Send(packet);
                        LastChangesPackets++;
                    }
                }
            }
        }

        private void ResendDirtyChunks()
        {
            foreach (var world in _worldList)
            {
                if (world.DirtyChunks.IsEmpty) continue;

                var dirty = world.DirtyChunks.Keys.ToList();
                foreach (var pos in dirty)
                {
                    world.DirtyChunks.TryRemove(pos, out _);

                    if (!world.LoadedChunks.TryGetValue(pos, out var chunk)) continue;

                    foreach (var session in _sessions)
                    {
                        if (session.World != world || !session.SentChunks.Contains(pos)) continue;
                        session.Connection.Send(ChunkDataPacket.From(chunk));
                    }
                }
            }
        }

        /// <summary>Detects players standing in a portal block and, after a short soak, kicks off a transfer to
        /// the linked dimension. A freshly-arrived player is immune until they step off the portal so they don't
        /// bounce straight back. No-op when no content registered portal rules.</summary>
        private void UpdatePortals()
        {
            var portals = GameRegistry.Portals;
            if (portals == null) return;

            foreach (var session in _sessions)
            {
                if (!session.LoggedIn || session.PendingPortalWorld != null) continue;

                if (!TryFindPortalCell(portals, session, out var cell))
                {
                    session.PortalTimer = 0;
                    session.PortalImmune = false;
                    continue;
                }

                if (session.PortalImmune) continue;
                if (++session.PortalTimer < PortalSoakTicks) continue;

                BeginTransfer(session, cell, portals);
            }
        }

        /// <summary>True if any cell the player's body occupies is a portal block (their column from feet to
        /// head). Checking the whole body — not just the floored feet block — matters because a player standing
        /// on the obsidian sill settles a hair below the portal's bottom block (feet floor to the sill), which
        /// would otherwise miss the portal every tick. Returns the matched cell (used as the transfer source).</summary>
        private static bool TryFindPortalCell(IDimensionPortals portals, ClientSession session, out Vector3i cell)
        {
            var p = session.Player.Position;
            var bx = (int) MathF.Floor(p.X);
            var bz = (int) MathF.Floor(p.Z);
            // Lift a touch off the floor so standing jitter (feet at y-0.0001) doesn't drop a block.
            var by = (int) MathF.Floor(p.Y + 0.2f);
            for (var dy = 0; dy <= 1; dy++)
            {
                cell = new Vector3i(bx, by + dy, bz);
                if (portals.IsPortalBlock(session.World.GetBlock(cell.X, cell.Y, cell.Z))) return true;
            }
            cell = default;
            return false;
        }

        /// <summary>Moves the player into the linked dimension at the scaled coordinates, tells the client to drop
        /// its world and switch render mode, and arms the pending-portal build. The destination portal itself is
        /// built (and the join handshake replayed) by <see cref="ProcessPendingTransfers"/> once that column has
        /// streamed in server-side.</summary>
        private void BeginTransfer(ClientSession session, Vector3i feet, IDimensionPortals portals)
        {
            var fromKey = session.World.DimensionKey;
            var toKey = portals.TargetDimension(fromKey);
            if (toKey == null) return;

            var toWorld = GetOrCreateWorld(toKey);
            var approx = portals.ScaleToTarget(fromKey, toKey, feet);

            session.World.RemovePlayer(session.Player);
            BroadcastTo(session.World, new EntityDespawnPacket {EntityId = session.EntityId}, session);

            session.Player.Position = approx.ToVector3() + new Vector3(0.5f, 0f, 0.5f);
            MoveToDimension(session, toWorld, approx, buildPortal: true);
            session.PortalImmune = true;
            session.PortalTimer = 0;
        }

        /// <summary>Sends a dead player home to the Overworld spawn, transferring dimensions if they died
        /// somewhere else (so the client drops the Nether and re-loads the Overworld).</summary>
        private void BeginRespawnTransfer(ClientSession session)
        {
            session.Player.Position = SpawnPosition;
            MoveToDimension(session, _world, SpawnPosition.ToVector3i(), buildPortal: false);
        }

        /// <summary>Detaches the player from their current dimension and re-attaches them to <paramref
        /// name="toWorld"/> at <paramref name="approx"/>, resets the per-session streaming state, and tells the
        /// client to drop its world + apply the destination's visuals. The arrival (portal build or plain drop)
        /// is finalized by <see cref="ProcessPendingTransfers"/> once the column has streamed in.</summary>
        private void MoveToDimension(ClientSession session, WorldServer toWorld, Vector3i approx, bool buildPortal)
        {
            session.World.RemovePlayer(session.Player);
            BroadcastTo(session.World, new EntityDespawnPacket {EntityId = session.EntityId}, session);

            session.World = toWorld;
            session.Player.Velocity = Vector3.Zero;
            toWorld.AddPlayer(session.Player);

            session.SentChunks.Clear();
            session.SentLodRegions.Clear();
            session.StreamScanChunk = new Vector3i(int.MinValue);
            session.StreamScanLoadedCount = -1;
            session.LodScanChunk = new Vector3i(int.MinValue);
            session.LodScanRegionCount = -1;
            session.ReadyGate = null;
            session.ReadySent = false;
            session.PendingPortalWorld = toWorld;
            session.PendingPortalApprox = approx;
            session.PendingBuildPortal = buildPortal;

            // Ship the destination dimension's generic visuals (sky/fog/ambient) so the client renders it without
            // the engine knowing anything dimension-specific. Defaults (open sky) if the dimension is unregistered.
            var dim = new DimensionChangePacket();
            if (GameRegistry.TryGetDimension(toWorld.DimensionKey, out var def))
            {
                dim.HasSky = def.HasSky;
                dim.FogColor = def.FogColor;
                dim.AmbientLight = def.AmbientLight;
            }
            session.Connection.Send(dim);
        }

        /// <summary>For each in-flight transfer, waits until the destination column has streamed into the target
        /// world, then builds/links the portal and replays the join handshake (LoginAccept + entity sync) so the
        /// client finishes loading at the destination portal.</summary>
        private void ProcessPendingTransfers()
        {
            foreach (var session in _sessions)
            {
                var world = session.PendingPortalWorld;
                if (world == null) continue;

                // Wait until the destination column has generated (IsChunkGenerated covers all-air chunks the
                // open Overworld sky produces, which never reach LoadedChunks); the portal build's SetBlock
                // creates any cells it spills into.
                var chunk = WorldBase.ChunkInWorld(session.PendingPortalApprox);
                if (!world.IsChunkGenerated(chunk)) continue;

                // A portal transfer finds-or-builds the destination portal; a respawn just drops the player at
                // the (known-safe) spawn block.
                var portals = GameRegistry.Portals;
                var stand = session.PendingBuildPortal && portals != null
                    ? portals.EnsureDestinationPortal(world, session.PendingPortalApprox)
                    : session.PendingPortalApprox.ToVector3() + new Vector3(0.5f, 0f, 0.5f);
                session.Player.Position = stand;
                session.Player.Velocity = Vector3.Zero;
                session.PendingPortalWorld = null;

                session.ReadyGate = stand;
                session.ReadySent = false;
                session.Connection.Send(new LoginAcceptPacket {EntityId = session.EntityId, Spawn = stand});
                session.Connection.Send(new WorldTimePacket {WorldSeconds = world.WorldTimeSeconds});
                SendPresentEntities(session);

                BroadcastTo(world, new EntitySpawnPacket
                {
                    EntityId = session.EntityId,
                    Position = stand,
                    Pitch = session.Player.Pitch,
                    Yaw = session.Player.Yaw
                }, session);

                Logger.Info($"Player {session.EntityId} entered \"{world.DimensionKey}\"");
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
                    PlayerSerializer.Save(_world.WorldDir, session.PlayerName, session.Inventory, session.Player,
                        session.World.DimensionKey);

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

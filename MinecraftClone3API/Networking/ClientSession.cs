using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using Silk.NET.Maths;

namespace MinecraftClone3API.Networking
{
    /// <summary>Server-side state for one connected client.</summary>
    public class ClientSession
    {
        public readonly IConnection Connection;
        public readonly HashSet<Vector3D<int>> SentChunks = new HashSet<Vector3D<int>>();

        public EntityPlayer Player;
        public int EntityId;
        public bool LoggedIn;
        public bool ReadySent;

        /// <summary>The dimension world this player is currently in. Chunk streaming, entity relay, and block
        /// deltas are all scoped to it; changed by a portal transfer.</summary>
        public WorldServer World;

        /// <summary>The position whose spawn column must be streamed before the client is told it may finish
        /// (re)loading (<see cref="PlayerReadyPacket"/>). Set on login (world spawn) and on a portal transfer
        /// (the destination). Null once consumed.</summary>
        public Vector3D<float>? ReadyGate;

        /// <summary>Portal transfer state. <see cref="PortalTimer"/> counts ticks the player has stood in a
        /// portal block; <see cref="PortalImmune"/> is set right after a transfer and cleared once the player
        /// steps off a portal, so arriving inside one doesn't bounce straight back. A non-null
        /// <see cref="PendingPortalWorld"/> means a transfer is mid-flight: once
        /// <see cref="PendingPortalApprox"/>'s column has streamed into that world the destination is finalized —
        /// building/linking a portal when <see cref="PendingBuildPortal"/> (a portal transfer) or just dropping
        /// the player there when not (a respawn back to the Overworld spawn).</summary>
        public int PortalTimer;
        public bool PortalImmune;
        public WorldServer PendingPortalWorld;
        public Vector3D<int> PendingPortalApprox;
        public bool PendingBuildPortal;

        public string PlayerName = "";

        /// <summary>The authoritative server-side inventory for this client; synced to the client on join via
        /// <see cref="InventoryStatePacket"/>, then mutated by <see cref="InventoryActionPacket"/>s and
        /// persisted per player on disconnect/shutdown.</summary>
        public readonly Inventory Inventory = new Inventory();

        /// <summary>The container block this client currently has open (a furnace), or null. While set, the
        /// server streams that block's <see cref="ContainerStatePacket"/> each tick so the screen shows live
        /// progress; cleared on <see cref="CloseContainerPacket"/>.</summary>
        public Vector3D<int>? OpenContainer;

        /// <summary>Whether this player is currently dead (health ≤ 0), set by the stats sync. While dead the
        /// server holds the player until a <see cref="RespawnRequestPacket"/> arrives.</summary>
        public bool Dead;

        // Last survival stats sent to this client, so PlayerStatsPacket is sent only on change. StatsSent
        // forces the first send (the cached values would otherwise compare equal to a fresh full-health player).
        public bool StatsSent;
        public float LastHealth;
        public float LastHunger;
        public float LastSaturation;
        public byte LastGameMode;
        public bool LastDead;

        // Gate for StreamChunks: the player chunk + loaded-chunk count at the last fully-drained interest
        // scan. When neither changed there is nothing new to stream, so the O(loaded) ConcurrentDictionary
        // scan is skipped (it was ~88% of CPU in a trace while standing still in a fully-streamed area).
        // Sentinel forces the first scan.
        public Vector3D<int> StreamScanChunk = new Vector3D<int>(int.MinValue, int.MinValue, int.MinValue);
        public int StreamScanLoadedCount = -1;

        // Phase-2 LOD horizon: regions streamed to this client, plus the same gate StreamChunks uses
        // (player chunk + LOD-store region count) so the region scan is skipped when nothing changed.
        public readonly HashSet<Vector3D<int>> SentLodRegions = new HashSet<Vector3D<int>>();
        public Vector3D<int> LodScanChunk = new Vector3D<int>(int.MinValue, int.MinValue, int.MinValue);
        public int LodScanRegionCount = -1;

        public ClientSession(IConnection connection)
        {
            Connection = connection;
        }
    }
}

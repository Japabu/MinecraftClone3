using System.Collections.Generic;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Networking
{
    /// <summary>Server-side state for one connected client.</summary>
    public class ClientSession
    {
        public readonly IConnection Connection;
        public readonly HashSet<Vector3i> SentChunks = new HashSet<Vector3i>();

        public EntityPlayer Player;
        public int EntityId;
        public bool LoggedIn;
        public bool ReadySent;

        public string PlayerName = "";

        /// <summary>The authoritative server-side inventory for this client; synced to the client on join via
        /// <see cref="InventoryStatePacket"/>, then mutated by <see cref="InventoryActionPacket"/>s and
        /// persisted per player on disconnect/shutdown.</summary>
        public readonly Inventory Inventory = new Inventory();

        /// <summary>The container block this client currently has open (a furnace), or null. While set, the
        /// server streams that block's <see cref="ContainerStatePacket"/> each tick so the screen shows live
        /// progress; cleared on <see cref="CloseContainerPacket"/>.</summary>
        public Vector3i? OpenContainer;

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
        public Vector3i StreamScanChunk = new Vector3i(int.MinValue);
        public int StreamScanLoadedCount = -1;

        // Phase-2 LOD horizon: regions streamed to this client, plus the same gate StreamChunks uses
        // (player chunk + LOD-store region count) so the region scan is skipped when nothing changed.
        public readonly HashSet<Vector3i> SentLodRegions = new HashSet<Vector3i>();
        public Vector3i LodScanChunk = new Vector3i(int.MinValue);
        public int LodScanRegionCount = -1;

        public ClientSession(IConnection connection)
        {
            Connection = connection;
        }
    }
}

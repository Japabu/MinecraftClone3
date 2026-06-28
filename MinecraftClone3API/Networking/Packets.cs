using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.Networking
{
    /// <summary>Client announces itself to the server.</summary>
    public class LoginPacket : Packet
    {
        public string Name = "";

        public override PacketId Id => PacketId.Login;
        public override void Write(BinaryWriter writer) => writer.Write(Name ?? "");
        public override void Read(BinaryReader reader) => Name = reader.ReadString();
    }

    /// <summary>Server accepts a login, assigning the client its entity id and spawn position.</summary>
    public class LoginAcceptPacket : Packet
    {
        public int EntityId;
        public Vector3D<float> Spawn;

        public override PacketId Id => PacketId.LoginAccept;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(EntityId);
            WriteVector3(writer, Spawn);
        }

        public override void Read(BinaryReader reader)
        {
            EntityId = reader.ReadInt32();
            Spawn = ReadVector3(reader);
        }
    }

    /// <summary>Server tells the client the spawn-area chunks have been streamed, so it may apply the
    /// spawn and hand control to the player. Same packet path over loopback (singleplayer) and TCP
    /// (multiplayer), so the client's join/loading flow is identical for both.</summary>
    public class PlayerReadyPacket : Packet
    {
        public override PacketId Id => PacketId.PlayerReady;
        public override void Write(BinaryWriter writer) { }
        public override void Read(BinaryReader reader) { }
    }

    /// <summary>Server streams a full chunk. Over the loopback (singleplayer) the live
    /// <see cref="Chunk"/> is carried by reference and the client clones it directly — no serialize,
    /// compress, or decompress at all. Over TCP the chunk is serialized and GZip-compressed lazily in
    /// <see cref="Write"/> (the transport boundary); <see cref="Read"/> only copies the still-compressed
    /// bytes into <see cref="CompressedData"/> (a cheap memcpy on the receive/main thread), and the
    /// client decompresses + deserializes them on its background apply thread, off the render thread.
    /// The wire format is unchanged.</summary>
    public class ChunkDataPacket : Packet
    {
        public Vector3D<int> Position;

        /// <summary>The live server chunk, set on the loopback path; null after a TCP <see cref="Read"/>.</summary>
        public Chunk Chunk;

        /// <summary>Still-GZip'd chunk bytes from a TCP <see cref="Read"/>, decompressed + deserialized by
        /// the client's apply thread; null on the loopback path (the client clones <see cref="Chunk"/>).</summary>
        public byte[] CompressedData;

        public override PacketId Id => PacketId.ChunkData;

        public static ChunkDataPacket From(Chunk chunk) => new ChunkDataPacket {Position = chunk.Position, Chunk = chunk};

        public override void Write(BinaryWriter writer)
        {
            byte[] raw;
            using (var stream = new MemoryStream())
            {
                using (var bw = new BinaryWriter(stream))
                    Chunk.Write(bw);
                raw = stream.ToArray();
            }

            var compressed = CompressionHelper.CompressBytes(raw);
            WriteVector3i(writer, Position);
            writer.Write(compressed.Length);
            writer.Write(compressed);
        }

        public override void Read(BinaryReader reader)
        {
            Position = ReadVector3i(reader);
            CompressedData = reader.ReadBytes(reader.ReadInt32());
        }
    }

    /// <summary>Client tells the server it dropped a chunk from its cache, so the server stops
    /// resending changes for it and may stream it fresh again if the client returns. Chunk lifetime
    /// is client-owned; the server never decides a client unload.</summary>
    public class ChunkReleasePacket : Packet
    {
        public Vector3D<int> Position;

        public override PacketId Id => PacketId.ChunkRelease;
        public override void Write(BinaryWriter writer) => WriteVector3i(writer, Position);
        public override void Read(BinaryReader reader) => Position = ReadVector3i(reader);
    }

    /// <summary>Server announces a batch of authoritative block/light changes within a single chunk.
    /// This is the transport for edits and light propagation; whole-chunk <see cref="ChunkDataPacket"/>
    /// is used only for initial streaming. Each entry packs its in-chunk index, block id, light and sky,
    /// so the client mutates the chunk in place and remeshes only it (plus face neighbours) instead of
    /// decompressing + deserializing a whole resent chunk on the main thread.</summary>
    public class BlockChangesPacket : Packet
    {
        public Vector3D<int> ChunkPos;
        public List<BlockChange> Changes;

        public override PacketId Id => PacketId.BlockChanges;

        public override void Write(BinaryWriter writer)
        {
            WriteVector3i(writer, ChunkPos);
            writer.Write(Changes.Count);
            foreach (var change in Changes)
            {
                writer.Write(change.LocalIndex);
                writer.Write(change.BlockId);
                writer.Write(change.Light);
                writer.Write(change.Sky);
            }
        }

        public override void Read(BinaryReader reader)
        {
            ChunkPos = ReadVector3i(reader);
            var count = reader.ReadInt32();
            Changes = new List<BlockChange>(count);
            for (var i = 0; i < count; i++)
                Changes.Add(new BlockChange(ChunkPos, reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(),
                    reader.ReadUInt16()));
        }
    }

    /// <summary>Client asks the server to place a block (id 0 = break).</summary>
    public class PlaceBlockRequestPacket : Packet
    {
        public Vector3D<int> Position;
        public ushort BlockId;
        public int Metadata;

        public override PacketId Id => PacketId.PlaceBlockRequest;

        public override void Write(BinaryWriter writer)
        {
            WriteVector3i(writer, Position);
            writer.Write(BlockId);
            writer.Write(Metadata);
        }

        public override void Read(BinaryReader reader)
        {
            Position = ReadVector3i(reader);
            BlockId = reader.ReadUInt16();
            Metadata = reader.ReadInt32();
        }
    }

    /// <summary>Client asks the server to use the held (non-block) item — e.g. a spawn egg — toward a cell.
    /// The server reads its own authoritative copy of the held item, so the request can't spoof which item.</summary>
    public class UseItemRequestPacket : Packet
    {
        public Vector3D<int> Position;

        public override PacketId Id => PacketId.UseItemRequest;
        public override void Write(BinaryWriter writer) => WriteVector3i(writer, Position);
        public override void Read(BinaryReader reader) => Position = ReadVector3i(reader);
    }

    /// <summary>Client → server: right-clicked the held item while aiming at an entity (e.g. shears on a sheep).
    /// The server resolves the target from its own entity list and runs the held item's <c>OnUseOnEntity</c>.</summary>
    public class UseItemOnEntityRequestPacket : Packet
    {
        public int EntityId;

        public override PacketId Id => PacketId.UseItemOnEntityRequest;
        public override void Write(BinaryWriter writer) => writer.Write(EntityId);
        public override void Read(BinaryReader reader) => EntityId = reader.ReadInt32();
    }

    /// <summary>Client → server: left-clicked an entity to attack it. The server resolves the target from its
    /// own entity list and applies the held weapon's damage; mob health/death are server-authoritative.</summary>
    public class AttackEntityRequestPacket : Packet
    {
        public int EntityId;

        public override PacketId Id => PacketId.AttackEntityRequest;
        public override void Write(BinaryWriter writer) => writer.Write(EntityId);
        public override void Read(BinaryReader reader) => EntityId = reader.ReadInt32();
    }

    /// <summary>Server → client: an entity's <see cref="EntityData"/> changed (e.g. a sheep was sheared), so the
    /// client replaces its copy. The data is type-tagged, so any registered subclass round-trips.</summary>
    public class EntityDataPacket : Packet
    {
        public int EntityId;
        public EntityData Data;

        public override PacketId Id => PacketId.EntityData;
        public override void Write(BinaryWriter writer)
        {
            writer.Write(EntityId);
            EntityData.Write(writer, Data);
        }

        public override void Read(BinaryReader reader)
        {
            EntityId = reader.ReadInt32();
            Data = EntityData.Read(reader);
        }
    }

    /// <summary>Server → client full inventory sync (the server owns the authoritative copy). Sent on join
    /// after login; the client then mutates its replica optimistically and reports changes back via
    /// <see cref="InventoryActionPacket"/>.</summary>
    public class InventoryStatePacket : Packet
    {
        public Inventory Inventory = new Inventory();

        public override PacketId Id => PacketId.InventoryState;
        public override void Write(BinaryWriter writer) => Inventory.Write(writer);
        public override void Read(BinaryReader reader) => Inventory.Read(reader);
    }

    /// <summary>Client → server: set a single inventory slot to a stack. In creative the client computes the
    /// result of a click/drag locally (cursor handling is client-side) and tells the server the final slot
    /// contents; the server trusts it (creative is infinite, placement is already client-authoritative).</summary>
    public class InventoryActionPacket : Packet
    {
        public int SlotIndex;
        public ItemStack Stack;

        public override PacketId Id => PacketId.InventoryAction;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(SlotIndex);
            Stack.Write(writer);
        }

        public override void Read(BinaryReader reader)
        {
            SlotIndex = reader.ReadInt32();
            Stack = ItemStack.Read(reader);
        }
    }

    /// <summary>Client → server: the selected hotbar slot changed (number keys / scroll wheel), so the
    /// server knows which item the player would place.</summary>
    public class HeldSlotPacket : Packet
    {
        public int SelectedHotbar;

        public override PacketId Id => PacketId.HeldSlot;
        public override void Write(BinaryWriter writer) => writer.Write(SelectedHotbar);
        public override void Read(BinaryReader reader) => SelectedHotbar = reader.ReadInt32();
    }

    /// <summary>Client→server request to drop the held hotbar item: one item, or the whole stack
    /// (<see cref="All"/>, Ctrl+Q). The server decrements its authoritative inventory, spawns the dropped
    /// item entity in front of the player, and echoes the new inventory back.</summary>
    public class DropItemRequestPacket : Packet
    {
        public bool All;

        public override PacketId Id => PacketId.DropItemRequest;
        public override void Write(BinaryWriter writer) => writer.Write(All);
        public override void Read(BinaryReader reader) => All = reader.ReadBoolean();
    }

    /// <summary>Drops an arbitrary client-side stack (crafting-grid / cursor leftovers that don't fit on close)
    /// into the world. Unlike <see cref="DropItemRequestPacket"/> this carries the stack itself and the server
    /// spawns it without touching the authoritative inventory (the stack never lived there).</summary>
    public class DropStackRequestPacket : Packet
    {
        public ItemStack Stack = ItemStack.Empty;

        public override PacketId Id => PacketId.DropStackRequest;
        public override void Write(BinaryWriter writer) => Stack.Write(writer);
        public override void Read(BinaryReader reader) => Stack = ItemStack.Read(reader);
    }

    /// <summary>Entity position/orientation update (client→server for the local player, relayed to others).</summary>
    public class EntityMovePacket : Packet
    {
        public int EntityId;
        public Vector3D<float> Position;
        public float Pitch;
        public float Yaw;

        /// <summary>Ticks remaining on the damage flash (0 normally); the client renders the entity red while
        /// non-zero so a hit reads. Streamed each tick with the position.</summary>
        public byte HurtTime;

        /// <summary>The session-local id of the item the entity holds in its main hand (0 = nothing), so other
        /// clients can draw it. Session-local ids are safe on the live wire (client and server share the same
        /// plugin load), as with <see cref="BlockChangesPacket"/>. Streamed each tick with the position.</summary>
        public ushort HeldItemId;

        public override PacketId Id => PacketId.EntityMove;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(EntityId);
            WriteVector3(writer, Position);
            writer.Write(Pitch);
            writer.Write(Yaw);
            writer.Write(HurtTime);
            writer.Write(HeldItemId);
        }

        public override void Read(BinaryReader reader)
        {
            EntityId = reader.ReadInt32();
            Position = ReadVector3(reader);
            Pitch = reader.ReadSingle();
            Yaw = reader.ReadSingle();
            HurtTime = reader.ReadByte();
            HeldItemId = reader.ReadUInt16();
        }
    }

    /// <summary>Server tells the client a remote entity appeared. <see cref="TypeId"/> selects the species
    /// (<see cref="EntityType.PlayerTypeId"/> = a remote player); <see cref="Stack"/> carries the item for a
    /// dropped-item entity and is empty otherwise.</summary>
    public class EntitySpawnPacket : Packet
    {
        public int EntityId;
        public ushort TypeId = EntityType.PlayerTypeId;
        public ItemStack Stack = ItemStack.Empty;
        public Vector3D<float> Position;
        public float Pitch;
        public float Yaw;
        public EntityData Data;

        public override PacketId Id => PacketId.EntitySpawn;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(EntityId);
            writer.Write(TypeId);
            Stack.Write(writer);
            WriteVector3(writer, Position);
            writer.Write(Pitch);
            writer.Write(Yaw);
            EntityData.Write(writer, Data);
        }

        public override void Read(BinaryReader reader)
        {
            EntityId = reader.ReadInt32();
            TypeId = reader.ReadUInt16();
            Stack = ItemStack.Read(reader);
            Position = ReadVector3(reader);
            Pitch = reader.ReadSingle();
            Yaw = reader.ReadSingle();
            Data = EntityData.Read(reader);
        }
    }

    /// <summary>Server tells the client a remote entity disappeared.</summary>
    public class EntityDespawnPacket : Packet
    {
        public int EntityId;

        public override PacketId Id => PacketId.EntityDespawn;
        public override void Write(BinaryWriter writer) => writer.Write(EntityId);
        public override void Read(BinaryReader reader) => EntityId = reader.ReadInt32();
    }

    /// <summary>Server → client world clock (seconds the world has simulated, = TickCount·SecondsPerTick).
    /// Sent on join and periodically; the client advances it locally between packets so the day/night cycle
    /// is server-authoritative and shared across multiplayer clients.</summary>
    public class WorldTimePacket : Packet
    {
        public double WorldSeconds;

        public override PacketId Id => PacketId.WorldTime;
        public override void Write(BinaryWriter writer) => writer.Write(WorldSeconds);
        public override void Read(BinaryReader reader) => WorldSeconds = reader.ReadDouble();
    }

    /// <summary>Server streams one Phase-2 LOD region (a footprint of surface-only columns for the distant
    /// horizon). Mirrors <see cref="ChunkDataPacket"/> exactly: over the loopback the live (immutable)
    /// <see cref="LodColumn"/> is carried by reference; over TCP it's serialized + GZip'd lazily in
    /// <see cref="Write"/>, and <see cref="Read"/> only copies the still-compressed bytes for the client's
    /// apply thread to decompress off the render thread.</summary>
    public class LodColumnDataPacket : Packet
    {
        public Vector3D<int> Position;
        public LodColumn LodColumn;
        public byte[] CompressedData;

        public override PacketId Id => PacketId.LodColumnData;

        public static LodColumnDataPacket From(LodColumn region)
            => new LodColumnDataPacket {Position = region.Position, LodColumn = region};

        public override void Write(BinaryWriter writer)
        {
            byte[] raw;
            using (var stream = new MemoryStream())
            {
                using (var bw = new BinaryWriter(stream))
                    LodColumn.Write(bw);
                raw = stream.ToArray();
            }

            var compressed = CompressionHelper.CompressBytes(raw);
            WriteVector3i(writer, Position);
            writer.Write(compressed.Length);
            writer.Write(compressed);
        }

        public override void Read(BinaryReader reader)
        {
            Position = ReadVector3i(reader);
            CompressedData = reader.ReadBytes(reader.ReadInt32());
        }
    }

    /// <summary>Server → client: the player is being transferred to another dimension. The client drops its
    /// entire cached world (chunks, entities, containers) and re-enters the loading flow, applying the generic
    /// per-dimension visuals carried here (sky on/off, fog colour, ambient floor — the engine knows no specific
    /// dimension). The new spawn position arrives afterwards in a <see cref="LoginAcceptPacket"/> once the
    /// destination portal has been built, reusing the join handshake.</summary>
    public class DimensionChangePacket : Packet
    {
        public bool HasSky = true;
        public Vector3D<float> FogColor;
        public Vector3D<float> AmbientLight;

        public override PacketId Id => PacketId.DimensionChange;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(HasSky);
            WriteVector3(writer, FogColor);
            WriteVector3(writer, AmbientLight);
        }

        public override void Read(BinaryReader reader)
        {
            HasSky = reader.ReadBoolean();
            FogColor = ReadVector3(reader);
            AmbientLight = ReadVector3(reader);
        }
    }

    /// <summary>Client opened the container block at <see cref="Position"/> (e.g. a furnace); the server then
    /// streams that block's <see cref="ContainerStatePacket"/> to this client while it stays open.</summary>
    public class OpenContainerPacket : Packet
    {
        public Vector3D<int> Position;
        public override PacketId Id => PacketId.OpenContainer;
        public override void Write(BinaryWriter writer) => WriteVector3i(writer, Position);
        public override void Read(BinaryReader reader) => Position = ReadVector3i(reader);
    }

    /// <summary>Client closed its open container screen; the server stops streaming its state.</summary>
    public class CloseContainerPacket : Packet
    {
        public override PacketId Id => PacketId.CloseContainer;
        public override void Write(BinaryWriter writer) { }
        public override void Read(BinaryReader reader) { }
    }

    /// <summary>Server → client live snapshot of a container block the client has open: its item
    /// <see cref="Slots"/> and integer progress <see cref="Fields"/> (e.g. furnace burn/cook counters). The
    /// client copies these into its view by value (the loopback transport carries the live arrays by reference).</summary>
    public class ContainerStatePacket : Packet
    {
        public Vector3D<int> Position;
        public ItemStack[] Slots;
        public int[] Fields;

        public override PacketId Id => PacketId.ContainerState;

        public override void Write(BinaryWriter writer)
        {
            WriteVector3i(writer, Position);
            writer.Write((byte) Slots.Length);
            foreach (var slot in Slots) slot.Write(writer);
            writer.Write((byte) Fields.Length);
            foreach (var field in Fields) writer.Write(field);
        }

        public override void Read(BinaryReader reader)
        {
            Position = ReadVector3i(reader);
            Slots = new ItemStack[reader.ReadByte()];
            for (var i = 0; i < Slots.Length; i++) Slots[i] = ItemStack.Read(reader);
            Fields = new int[reader.ReadByte()];
            for (var i = 0; i < Fields.Length; i++) Fields[i] = reader.ReadInt32();
        }
    }

    /// <summary>Client → server edit of one item slot of an open container block (trusted, as inventory edits
    /// are). The server applies it to the block's <see cref="MinecraftClone3API.Blocks.ContainerBlockData"/>.</summary>
    public class ContainerSlotPacket : Packet
    {
        public Vector3D<int> Position;
        public int Slot;
        public ItemStack Stack;

        public override PacketId Id => PacketId.ContainerSlot;

        public override void Write(BinaryWriter writer)
        {
            WriteVector3i(writer, Position);
            writer.Write(Slot);
            Stack.Write(writer);
        }

        public override void Read(BinaryReader reader)
        {
            Position = ReadVector3i(reader);
            Slot = reader.ReadInt32();
            Stack = ItemStack.Read(reader);
        }
    }

    /// <summary>Server → owning client snapshot of the player's survival stats (health/hunger/saturation,
    /// game mode, and the death flag). Sent on join and whenever a value changes; drives the survival HUD and
    /// the death screen.</summary>
    public class PlayerStatsPacket : Packet
    {
        public float Health;
        public float MaxHealth;
        public float Hunger;
        public float Saturation;
        public byte GameMode;
        public bool Dead;

        public override PacketId Id => PacketId.PlayerStats;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(Health);
            writer.Write(MaxHealth);
            writer.Write(Hunger);
            writer.Write(Saturation);
            writer.Write(GameMode);
            writer.Write(Dead);
        }

        public override void Read(BinaryReader reader)
        {
            Health = reader.ReadSingle();
            MaxHealth = reader.ReadSingle();
            Hunger = reader.ReadSingle();
            Saturation = reader.ReadSingle();
            GameMode = reader.ReadByte();
            Dead = reader.ReadBoolean();
        }
    }

    /// <summary>Client → server report of a completed fall (distance in blocks). The player is position-
    /// authoritative, so the client measures the fall and the server decides the damage.</summary>
    public class PlayerFallPacket : Packet
    {
        public float FallDistance;

        public override PacketId Id => PacketId.PlayerFall;
        public override void Write(BinaryWriter writer) => writer.Write(FallDistance);
        public override void Read(BinaryReader reader) => FallDistance = reader.ReadSingle();
    }

    /// <summary>Client → server request to switch game mode (the pause-menu toggle). Server-authoritative; the
    /// server flips the mode and echoes it back in the next <see cref="PlayerStatsPacket"/>.</summary>
    public class SetGameModeRequestPacket : Packet
    {
        public byte GameMode;

        public override PacketId Id => PacketId.SetGameModeRequest;
        public override void Write(BinaryWriter writer) => writer.Write(GameMode);
        public override void Read(BinaryReader reader) => GameMode = reader.ReadByte();
    }

    /// <summary>Client → server request to respawn after death (the death-screen button). Honoured only while
    /// the player is dead.</summary>
    public class RespawnRequestPacket : Packet
    {
        public override PacketId Id => PacketId.RespawnRequest;
        public override void Write(BinaryWriter writer) { }
        public override void Read(BinaryReader reader) { }
    }

    /// <summary>Server → client command to move the player to a position (a landed ender pearl). The player is
    /// position-authoritative, so this is the only relocation outside the respawn snap; the client obeys by
    /// snapping its local player there and clearing its fall accumulator.</summary>
    public class PlayerTeleportPacket : Packet
    {
        public Vector3D<float> Position;

        public override PacketId Id => PacketId.PlayerTeleport;
        public override void Write(BinaryWriter writer) => WriteVector3(writer, Position);
        public override void Read(BinaryReader reader) => Position = ReadVector3(reader);
    }
}

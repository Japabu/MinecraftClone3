using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

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
        public Vector3 Spawn;

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
        public Vector3i Position;

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
        public Vector3i Position;

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
        public Vector3i ChunkPos;
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
        public Vector3i Position;
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

    /// <summary>Entity position/orientation update (client→server for the local player, relayed to others).</summary>
    public class EntityMovePacket : Packet
    {
        public int EntityId;
        public Vector3 Position;
        public float Pitch;
        public float Yaw;

        public override PacketId Id => PacketId.EntityMove;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(EntityId);
            WriteVector3(writer, Position);
            writer.Write(Pitch);
            writer.Write(Yaw);
        }

        public override void Read(BinaryReader reader)
        {
            EntityId = reader.ReadInt32();
            Position = ReadVector3(reader);
            Pitch = reader.ReadSingle();
            Yaw = reader.ReadSingle();
        }
    }

    /// <summary>Server tells the client a remote entity appeared.</summary>
    public class EntitySpawnPacket : Packet
    {
        public int EntityId;
        public Vector3 Position;
        public float Pitch;
        public float Yaw;

        public override PacketId Id => PacketId.EntitySpawn;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(EntityId);
            WriteVector3(writer, Position);
            writer.Write(Pitch);
            writer.Write(Yaw);
        }

        public override void Read(BinaryReader reader)
        {
            EntityId = reader.ReadInt32();
            Position = ReadVector3(reader);
            Pitch = reader.ReadSingle();
            Yaw = reader.ReadSingle();
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
        public Vector3i Position;
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
}

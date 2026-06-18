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

    /// <summary>Server streams a full chunk: its position and the GZip of <see cref="Chunk.Write"/>.</summary>
    public class ChunkDataPacket : Packet
    {
        public Vector3i Position;
        public byte[] CompressedData;

        public override PacketId Id => PacketId.ChunkData;

        public static ChunkDataPacket From(Chunk chunk)
        {
            byte[] raw;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                    chunk.Write(writer);
                raw = stream.ToArray();
            }

            return new ChunkDataPacket {Position = chunk.Position, CompressedData = CompressionHelper.CompressBytes(raw)};
        }

        public override void Write(BinaryWriter writer)
        {
            WriteVector3i(writer, Position);
            writer.Write(CompressedData.Length);
            writer.Write(CompressedData);
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

    /// <summary>Server announces an authoritative single-block change (light packed as a ushort).</summary>
    public class BlockChangePacket : Packet
    {
        public Vector3i Position;
        public ushort BlockId;
        public ushort Light;

        public override PacketId Id => PacketId.BlockChange;

        public override void Write(BinaryWriter writer)
        {
            WriteVector3i(writer, Position);
            writer.Write(BlockId);
            writer.Write(Light);
        }

        public override void Read(BinaryReader reader)
        {
            Position = ReadVector3i(reader);
            BlockId = reader.ReadUInt16();
            Light = reader.ReadUInt16();
        }
    }

    /// <summary>Client asks the server to place a block (id 0 = break).</summary>
    public class PlaceBlockRequestPacket : Packet
    {
        public Vector3i Position;
        public ushort BlockId;

        public override PacketId Id => PacketId.PlaceBlockRequest;

        public override void Write(BinaryWriter writer)
        {
            WriteVector3i(writer, Position);
            writer.Write(BlockId);
        }

        public override void Read(BinaryReader reader)
        {
            Position = ReadVector3i(reader);
            BlockId = reader.ReadUInt16();
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
}

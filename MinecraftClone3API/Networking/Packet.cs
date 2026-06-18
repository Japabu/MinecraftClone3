using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Networking
{
    public enum PacketId : byte
    {
        Login,
        LoginAccept,
        ChunkData,
        ChunkRelease,
        BlockChange,
        PlaceBlockRequest,
        EntityMove,
        EntitySpawn,
        EntityDespawn
    }

    /// <summary>
    /// Base for all wire messages. A packet serializes to its one-byte <see cref="Id"/> followed by
    /// its payload; <see cref="Deserialize"/> uses the id to build the matching empty packet and
    /// fills it via <see cref="Read"/>.
    /// </summary>
    public abstract class Packet
    {
        private static readonly Dictionary<PacketId, Func<Packet>> Factory = new Dictionary<PacketId, Func<Packet>>
        {
            {PacketId.Login, () => new LoginPacket()},
            {PacketId.LoginAccept, () => new LoginAcceptPacket()},
            {PacketId.ChunkData, () => new ChunkDataPacket()},
            {PacketId.ChunkRelease, () => new ChunkReleasePacket()},
            {PacketId.BlockChange, () => new BlockChangePacket()},
            {PacketId.PlaceBlockRequest, () => new PlaceBlockRequestPacket()},
            {PacketId.EntityMove, () => new EntityMovePacket()},
            {PacketId.EntitySpawn, () => new EntitySpawnPacket()},
            {PacketId.EntityDespawn, () => new EntityDespawnPacket()}
        };

        public abstract PacketId Id { get; }

        public abstract void Write(BinaryWriter writer);
        public abstract void Read(BinaryReader reader);

        public static byte[] Serialize(Packet packet)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte) packet.Id);
                packet.Write(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static Packet Deserialize(byte[] data)
        {
            using (var stream = new MemoryStream(data))
            using (var reader = new BinaryReader(stream))
            {
                var id = (PacketId) reader.ReadByte();
                var packet = Factory[id]();
                packet.Read(reader);
                return packet;
            }
        }

        protected static void WriteVector3i(BinaryWriter writer, Vector3i v)
        {
            writer.Write(v.X);
            writer.Write(v.Y);
            writer.Write(v.Z);
        }

        protected static Vector3i ReadVector3i(BinaryReader reader)
            => new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

        protected static void WriteVector3(BinaryWriter writer, Vector3 v)
        {
            writer.Write(v.X);
            writer.Write(v.Y);
            writer.Write(v.Z);
        }

        protected static Vector3 ReadVector3(BinaryReader reader)
            => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}

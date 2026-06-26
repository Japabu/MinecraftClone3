using System;
using System.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Entities
{
    /// <summary>
    /// Disk (de)serialization for world entities (mobs/animals/dropped items/falling blocks). Each record is
    /// length-prefixed so an entity of a now-missing type can be skipped without desyncing the rest. The entity
    /// type and any block/item ids inside are referenced by stable registry <b>name</b> (the same self-describing
    /// rule as chunks), so entities survive plugin add/remove/reorder. Players are persisted separately by
    /// <see cref="IO.PlayerSerializer"/>, never here.
    /// </summary>
    internal static class EntitySerializer
    {
        internal static void Write(BinaryWriter writer, Entity entity)
        {
            byte[] bytes;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(entity.Type.RegistryKey);
                WriteVector(bw, entity.Position);
                WriteVector(bw, entity.Velocity);
                bw.Write(entity.Yaw);
                bw.Write(entity.Pitch);
                entity.SerializeState(bw);
                bw.Flush();
                bytes = ms.ToArray();
            }

            writer.Write((ushort) bytes.Length);
            writer.Write(bytes);
        }

        /// <summary>Reads one entity record, or null when its type is unknown (plugin gone) or it fails to
        /// deserialize. The length-prefixed payload is always consumed, so a null return just drops that one
        /// entity. The returned entity is unspawned — the caller assigns its id via <c>WorldServer.SpawnEntity</c>.</summary>
        internal static Entity Read(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(reader.ReadUInt16());

            using (var ms = new MemoryStream(bytes))
            using (var br = new BinaryReader(ms))
            {
                var typeName = br.ReadString();
                if (!GameRegistry.EntityRegistry.TryGet(typeName, out var type))
                {
                    Logger.Warn($"Skipping unknown entity type \"{typeName}\"");
                    return null;
                }

                try
                {
                    var entity = type.CreateEntity();
                    entity.Position = ReadVector(br);
                    entity.Velocity = ReadVector(br);
                    entity.Yaw = br.ReadSingle();
                    entity.Pitch = br.ReadSingle();
                    entity.DeserializeState(br);
                    return entity;
                }
                catch (Exception e)
                {
                    Logger.Error($"Failed to deserialize entity \"{typeName}\": {e.Message}");
                    return null;
                }
            }
        }

        private static void WriteVector(BinaryWriter writer, Vector3 v)
        {
            writer.Write(v.X);
            writer.Write(v.Y);
            writer.Write(v.Z);
        }

        private static Vector3 ReadVector(BinaryReader reader)
            => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}

using System;
using System.IO;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Entities
{
    /// <summary>
    /// Polymorphic per-entity state held in a single <see cref="Entity.Data"/> slot — the entity analog of
    /// <see cref="Blocks.BlockData"/>. Concrete subclasses (e.g. a sheep's wool state) carry their own fields,
    /// are registered by type (<c>PluginContext.RegisterEntityData&lt;T&gt;</c>) into the
    /// <c>EntityDataRegistry</c>, and (de)serialize behind a registry-key tag so the receiver rebuilds the right
    /// subclass. Entities aren't persisted, so this is <b>wire-only</b>: it rides <c>EntitySpawnPacket</c> and
    /// <c>EntityDataPacket</c>, with no disk path.
    /// </summary>
    public abstract class EntityData
    {
        /// <summary>Whether the entity type's optional overlay render layer (the sheep's wool) is shown. Lets
        /// the GPU-free data drive the renderer without the API knowing the concrete subclass.</summary>
        public virtual bool OverlayVisible => true;

        public abstract void Serialize(BinaryWriter writer);
        public abstract void Deserialize(BinaryReader reader);

        // Wire form: a presence flag, then (when present) the registry-key tag + length-prefixed payload, so the
        // reader reconstructs the matching subclass. Mirrors BlockData's stream format (entities aren't saved).
        internal static void Write(BinaryWriter writer, EntityData data)
        {
            if (data == null)
            {
                writer.Write(false);
                return;
            }

            byte[] bytes;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                data.Serialize(bw);
                bw.Flush();
                bytes = ms.ToArray();
            }

            writer.Write(true);
            writer.Write(GameRegistry.GetEntityDataRegistryKey(data));
            writer.Write((ushort) bytes.Length);
            writer.Write(bytes);
        }

        internal static EntityData Read(BinaryReader reader)
        {
            if (!reader.ReadBoolean()) return null;

            var key = reader.ReadString();
            var bytes = reader.ReadBytes(reader.ReadUInt16());
            var data = (EntityData) Activator.CreateInstance(GameRegistry.GetEntityDataType(key));

            using (var ms = new MemoryStream(bytes))
            using (var br = new BinaryReader(ms))
                data.Deserialize(br);

            return data;
        }
    }
}

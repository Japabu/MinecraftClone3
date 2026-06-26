using System;
using System.IO;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Blocks
{
    public abstract class BlockData
    {
        internal static void WriteToStream(BlockData blockData, BinaryWriter writer)
        {
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                using (var bw = new BinaryWriter(ms))
                    try
                    {
                        blockData.Serialize(bw);
                    }
                    catch (Exception e)
                    {
                        Logger.Error("Error during serialization of " + blockData);
                        Logger.Exception(e);
                    }

                bytes = ms.ToArray();
            }

            writer.Write(GameRegistry.GetBlockDataRegistryKey(blockData));
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        /// <summary>Reads one block-data entry, or null when its type is unknown (its plugin is gone) or it
        /// fails to deserialize. The key + length-prefixed payload are always consumed first, so a null return
        /// just drops that one block's data and the rest of the chunk loads intact.</summary>
        internal static BlockData ReadFromStream(BinaryReader reader)
        {
            var blockDataRegistryKey = reader.ReadString();
            var bytesLength = reader.ReadUInt16();
            var bytes = reader.ReadBytes(bytesLength);

            if (!GameRegistry.BlockDataRegistry.TryGet(blockDataRegistryKey, out var entry))
            {
                Logger.Warn($"Skipping unknown block data \"{blockDataRegistryKey}\"");
                return null;
            }

            try
            {
                var blockData = (BlockData) Activator.CreateInstance(entry.Type);
                using (var ms = new MemoryStream(bytes))
                using (var br = new BinaryReader(ms))
                    blockData.Deserialize(br);
                return blockData;
            }
            catch (Exception e)
            {
                Logger.Error($"Failed to deserialize block data \"{blockDataRegistryKey}\": {e.Message}");
                return null;
            }
        }

        public abstract void Serialize(BinaryWriter writer);
        public abstract void Deserialize(BinaryReader reader);
    }
}

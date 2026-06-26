using System;
using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Entities;
using Silk.NET.Maths;

namespace MinecraftClone3API.Util
{
    internal class WorldSerializer
    {
        // Two parallel region stores under the world dir: chunk block data (.ri/.rd) and chunk entity data
        // (.rei/.red). They share the RegionStore format; only the magic differs so a stray file can't be
        // misread as the other kind.
        private const int ChunkMagic = 0x4D435233;  // "MCR3"
        private const int ChunkVersion = 1;
        private const int EntityMagic = 0x4D434533;  // "MCE3"
        private const int EntityVersion = 1;

        private readonly RegionStore _chunks;
        private readonly RegionStore _entities;

        public WorldSerializer(string worldDir)
        {
            _chunks = new RegionStore(worldDir, "Regions", ".ri", ".rd", ChunkMagic, ChunkVersion);
            _entities = new RegionStore(worldDir, "Regions", ".rei", ".red", EntityMagic, EntityVersion);
        }

        public void SaveChunk(Chunk chunk)
        {
            if (!chunk.NeedsSaving) return;
            _chunks.Save(chunk.Position, chunk.Write);
            chunk.NeedsSaving = false;
        }

        public CachedChunk LoadChunk(WorldBase world, Vector3D<int> chunkPos)
        {
            var chunkData = _chunks.Load(chunkPos);
            if (chunkData == null) return null;

            try
            {
                using (var reader = new BinaryReader(new MemoryStream(chunkData)))
                    return new CachedChunk(world, chunkPos, reader);
            }
            catch (Exception e)
            {
                // A chunk written by an older format version (or a truncated/corrupt entry) fails to
                // deserialize; treat it as absent so the load thread regenerates it instead of crashing.
                Logger.Error($"Failed to load chunk {chunkPos}, regenerating: {e.Message}");
                return null;
            }
        }

        /// <summary>Persists a chunk's entities (an empty list clears any stored blob), so they reload when the
        /// chunk does. Entities are addressed by their owning chunk; see <see cref="WorldServer"/>.</summary>
        public void SaveChunkEntities(Vector3D<int> chunkPos, List<Entity> entities)
        {
            if (entities.Count == 0)
            {
                _entities.Clear(chunkPos);
                return;
            }

            _entities.Save(chunkPos, writer =>
            {
                writer.Write(entities.Count);
                foreach (var entity in entities)
                    EntitySerializer.Write(writer, entity);
            });
        }

        /// <summary>The raw entity blob saved for a chunk (deserialized + spawned on the tick thread), or null.</summary>
        public byte[] LoadChunkEntities(Vector3D<int> chunkPos) => _entities.Load(chunkPos);
    }
}

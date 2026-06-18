using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;

namespace MinecraftClone3API.Util
{
    internal class WorldSerializer
    {
        /* 
         * Region index file format (ri)
         * RegionSizeCubed * (int chunkDataPos, int chunkDataLength)
         * 
         * Region data file format (rd)
         * Compressed chunk data
         */

        private const int ChunksInRegion = 128;
        private const int ChunksInRegionSquared = ChunksInRegion * ChunksInRegion;
        private const int ChunksInRegionCubed = ChunksInRegion * ChunksInRegion * ChunksInRegion;
        private const int RegionSize = ChunksInRegion * Chunk.Size;

        private const int IndexFileLength = ChunksInRegionCubed * sizeof(int) * 2;
        private const int IndexFileNull = -1;

        // Each decompressed region index is IndexFileLength (16 MB). A player's interest volume can
        // straddle up to 8 region files at once (the octants around a region corner); caching fewer
        // than that makes the load thread re-decompress 16 MB indices every pass it touches them,
        // which dominates allocation and starves the GC. Keep enough resident to cover the working
        // set plus roaming headroom.
        private const int MaxCachedIndexDatas = 16;

        private const string RegionsFolder = "Regions";

        private const string RegionIndexExt = ".ri";
        private const string RegionDataExt = ".rd";

        private static readonly List<Tuple<Vector3i, byte[]>> CachedIndexDatas = new List<Tuple<Vector3i, byte[]>>();

        private static readonly object IndexLockObject = new object();
        private static readonly object DataLockObject = new object();

        public static void SaveChunk(Chunk chunk)
        {
            if (!chunk.NeedsSaving) return;

            var region = ChunkToRegion(chunk.Position);
            var regionFilename = Path.Combine(GamePaths.WorldDir, RegionsFolder, GetRegionFilename(region));
            var indexFile = new FileInfo(regionFilename + RegionIndexExt);
            var dataFile = new FileInfo(regionFilename + RegionDataExt);
            // ReSharper disable once PossibleNullReferenceException
            indexFile.Directory.Create();

            lock (IndexLockObject)
            {
                if (!indexFile.Exists)
                    using (var stream = new GZipStream(indexFile.Create(), CompressionMode.Compress))
                    {
                        var buffer = new byte[1024];
                        for (var i = 0; i < buffer.Length; i += sizeof(int))
                            BitConverter.GetBytes(IndexFileNull).CopyTo(buffer, i);

                        for (var i = 0; i < IndexFileLength / buffer.Length; i++)
                            stream.Write(buffer, 0, buffer.Length);
                    }
            }

            //Compress chunk data
            byte[] compressedChunkData;
            using (var memoryStream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(memoryStream))
                {
                    chunk.Write(writer);
                }

                compressedChunkData = CompressionHelper.CompressBytes(memoryStream.ToArray());
            }

            var chunkDataPosition = dataFile.Exists ? (int) dataFile.Length : 0;
            var chunkDataLength = compressedChunkData.Length;

            //Append chunk to data file
            lock (DataLockObject)
            {
                using (var stream = dataFile.Open(FileMode.Append, FileAccess.Write))
                {
                    stream.Write(compressedChunkData, 0, compressedChunkData.Length);
                }
            }

            //Update chunk index
            var chunkIndexPosition = GetChunkIndexPosition(chunk.Position);

            lock (IndexLockObject)
            {
                var chunkIndexData = GetIndexData(region, indexFile);
                Array.Copy(BitConverter.GetBytes(chunkDataPosition), 0, chunkIndexData, chunkIndexPosition, sizeof(int));
                Array.Copy(BitConverter.GetBytes(chunkDataLength), 0, chunkIndexData, chunkIndexPosition + sizeof(int),
                    sizeof(int));
                File.WriteAllBytes(indexFile.FullName, CompressionHelper.CompressBytes(chunkIndexData));
            }

            chunk.NeedsSaving = false;
        }

        public static CachedChunk LoadChunk(WorldBase world, Vector3i chunkPos)
        {
            var region = ChunkToRegion(chunkPos);
            var regionFilename = Path.Combine(GamePaths.WorldDir, RegionsFolder, GetRegionFilename(region));
            var indexFile = new FileInfo(regionFilename + RegionIndexExt);
            var dataFile = new FileInfo(regionFilename + RegionDataExt);

            if (!indexFile.Exists || !dataFile.Exists) return null;

            //Get chunk data position and length
            int chunkDataPosition, chunkDataLength;
            var chunkIndexPosition = GetChunkIndexPosition(chunkPos);

            lock (IndexLockObject)
            {
                var chunkIndexData = GetIndexData(region, indexFile);
                // A region whose index file was truncated (e.g. the process was killed mid-save)
                // decompresses to fewer bytes than expected; treat it as unsaved rather than
                // reading past the end of the buffer and crashing the load thread.
                if (chunkIndexData.Length < chunkIndexPosition + sizeof(int) * 2)
                    return null;
                chunkDataPosition = BitConverter.ToInt32(chunkIndexData, chunkIndexPosition);
                chunkDataLength = BitConverter.ToInt32(chunkIndexData, chunkIndexPosition + sizeof(int));
            }

            if (chunkDataPosition == IndexFileNull || chunkDataLength == IndexFileNull) return null;
            //Read chunk data
            byte[] chunkData;

            lock (DataLockObject)
            {
                using (var reader = new BinaryReader(dataFile.OpenRead()))
                {
                    reader.BaseStream.Seek(chunkDataPosition, SeekOrigin.Begin);
                    chunkData = CompressionHelper.DecompressBytes(reader.ReadBytes(chunkDataLength));
                }
            }

            using (var reader = new BinaryReader(new MemoryStream(chunkData)))
            {
                return new CachedChunk(world, chunkPos, reader);
            }
        }

        private static string GetRegionFilename(Vector3i region) => $"{region.X} {region.Y} {region.Z}";

        private static byte[] GetIndexData(Vector3i region, FileInfo indexFile)
        {
            for (var i = 0; i < CachedIndexDatas.Count; i++)
            {
                if (CachedIndexDatas[i].Item1 != region) continue;

                // Move the hit to the end so eviction below stays least-recently-used: a player
                // cycling over its 8 in-range regions must never evict one of them for a stray access.
                var hit = CachedIndexDatas[i];
                CachedIndexDatas.RemoveAt(i);
                CachedIndexDatas.Add(hit);
                return hit.Item2;
            }

            var data = CompressionHelper.DecompressBytes(File.ReadAllBytes(indexFile.FullName));
            CachedIndexDatas.Add(new Tuple<Vector3i, byte[]>(region, data));

            if (CachedIndexDatas.Count > MaxCachedIndexDatas)
                CachedIndexDatas.RemoveAt(0);

            return data;
        }

        private static int GetChunkIndexPosition(Vector3i chunkPos)
        {
            var chunkInRegion = ChunkInRegion(chunkPos);
            return (ChunksInRegionSquared * chunkInRegion.X + ChunksInRegion * chunkInRegion.Y + chunkInRegion.Z) *
                   sizeof(int) * 2;
        }

        private static Vector3i ChunkToRegion(Vector3i v) => new Vector3i(
            v.X * Chunk.Size < 0 ? (v.X * Chunk.Size + 1) / RegionSize - 1 : v.X / ChunksInRegion,
            v.Y * Chunk.Size < 0 ? (v.Y * Chunk.Size + 1) / RegionSize - 1 : v.Y / ChunksInRegion,
            v.Z * Chunk.Size < 0 ? (v.Z * Chunk.Size + 1) / RegionSize - 1 : v.Z / ChunksInRegion);

        private static Vector3i ChunkInRegion(Vector3i v) => new Vector3i(
            v.X < 0 ? (v.X + 1) % ChunksInRegion + ChunksInRegion - 1 : v.X % ChunksInRegion,
            v.Y < 0 ? (v.Y + 1) % ChunksInRegion + ChunksInRegion - 1 : v.Y % ChunksInRegion,
            v.Z < 0 ? (v.Z + 1) % ChunksInRegion + ChunksInRegion - 1 : v.Z % ChunksInRegion);
    }
}
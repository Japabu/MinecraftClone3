using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using MinecraftClone3API.Blocks;
using Silk.NET.Maths;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Stores one arbitrary GZip-compressed blob per chunk, grouped into region files. Backs both the chunk
    /// block store and the chunk entity store — same on-disk shape, different magic. Format:
    /// <list type="bullet">
    /// <item>index (<c>.&lt;ext&gt;i</c>), GZip-compressed: int magic, int version, then
    /// <c>ChunksInRegionCubed × (int blobPos, int blobLength)</c> with <see cref="IndexFileNull"/> for empty slots.</item>
    /// <item>data (<c>.&lt;ext&gt;d</c>): concatenated independent GZip members, one per blob; the index points
    /// at <c>[pos, pos+len)</c>.</item>
    /// </list>
    /// The index is written atomically (temp + rename), so a crash never leaves a torn index. A region whose
    /// magic/version doesn't match (older format / foreign file) is treated as absent and regenerated. Re-saved
    /// blobs orphan their old bytes; a region self-<see cref="Compact"/>s once the data file is mostly dead.
    /// </summary>
    internal sealed class RegionStore
    {
        private const int ChunksInRegion = 32;
        private const int ChunksInRegionSquared = ChunksInRegion * ChunksInRegion;
        private const int ChunksInRegionCubed = ChunksInRegion * ChunksInRegion * ChunksInRegion;
        private const int RegionSize = ChunksInRegion * Chunk.Size;

        private const int IndexFileLength = ChunksInRegionCubed * sizeof(int) * 2;
        private const int IndexFileNull = -1;
        private const int HeaderLength = sizeof(int) * 2;

        // Cover a player's straddling interest scan plus roaming headroom so the load thread stops
        // re-decompressing region indices it just touched. At 256 KB each this is a few MB.
        private const int MaxCachedIndexDatas = 16;

        private const string TempExt = ".tmp";
        private const long CompactMinBytes = 1 << 20;
        private const double CompactDeadRatio = 2.0;

        private readonly List<Tuple<Vector3D<int>, byte[]>> _cachedIndexDatas = new List<Tuple<Vector3D<int>, byte[]>>();
        private readonly object _indexLock = new object();
        private readonly object _dataLock = new object();

        private readonly string _dir;
        private readonly string _indexExt;
        private readonly string _dataExt;
        private readonly int _magic;
        private readonly int _version;

        public RegionStore(string worldDir, string folder, string indexExt, string dataExt, int magic, int version)
        {
            _dir = Path.Combine(worldDir, folder);
            _indexExt = indexExt;
            _dataExt = dataExt;
            _magic = magic;
            _version = version;
        }

        private (FileInfo index, FileInfo data) FilesFor(Vector3D<int> region)
        {
            var path = Path.Combine(_dir, GetRegionFilename(region));
            return (new FileInfo(path + _indexExt), new FileInfo(path + _dataExt));
        }

        /// <summary>Appends a blob for <paramref name="chunkPos"/> (its old copy, if any, is orphaned) and
        /// commits the index atomically. <paramref name="write"/> streams the uncompressed blob.</summary>
        public void Save(Vector3D<int> chunkPos, Action<BinaryWriter> write)
        {
            var region = ChunkToRegion(chunkPos);
            var (indexFile, dataFile) = FilesFor(region);
            // ReSharper disable once PossibleNullReferenceException
            indexFile.Directory.Create();

            int blobPosition, blobLength;
            lock (_dataLock)
            {
                using (var fileStream = dataFile.Open(FileMode.Append, FileAccess.Write))
                {
                    blobPosition = (int) fileStream.Position;
                    var gzipStream = new GZipStream(fileStream, CompressionMode.Compress, true);
                    using (var writer = new BinaryWriter(gzipStream))
                        write(writer);
                    blobLength = (int) fileStream.Position - blobPosition;
                }
            }

            var indexPosition = GetChunkIndexPosition(chunkPos);
            bool shouldCompact;
            lock (_indexLock)
            {
                var table = GetIndexData(region, indexFile) ?? NewEmptyTable();
                Array.Copy(BitConverter.GetBytes(blobPosition), 0, table, indexPosition, sizeof(int));
                Array.Copy(BitConverter.GetBytes(blobLength), 0, table, indexPosition + sizeof(int), sizeof(int));
                WriteIndexData(region, indexFile, table);
                shouldCompact = ShouldCompact(table, dataFile);
            }

            if (shouldCompact) Compact(region, indexFile, dataFile);
        }

        /// <summary>Drops the blob for <paramref name="chunkPos"/> (no-op if there is none). Used when a chunk
        /// that previously had a blob now needs an empty one (e.g. its entities all left).</summary>
        public void Clear(Vector3D<int> chunkPos)
        {
            var region = ChunkToRegion(chunkPos);
            var (indexFile, _) = FilesFor(region);
            if (!indexFile.Exists) return;

            var indexPosition = GetChunkIndexPosition(chunkPos);
            lock (_indexLock)
            {
                var table = GetIndexData(region, indexFile);
                if (table == null) return;
                if (BitConverter.ToInt32(table, indexPosition) == IndexFileNull) return;

                var nul = BitConverter.GetBytes(IndexFileNull);
                Array.Copy(nul, 0, table, indexPosition, sizeof(int));
                Array.Copy(nul, 0, table, indexPosition + sizeof(int), sizeof(int));
                WriteIndexData(region, indexFile, table);
            }
        }

        /// <summary>The decompressed blob for <paramref name="chunkPos"/>, or null when absent, corrupt, or
        /// written in an older format/version.</summary>
        public byte[] Load(Vector3D<int> chunkPos)
        {
            var region = ChunkToRegion(chunkPos);
            var (indexFile, dataFile) = FilesFor(region);
            if (!indexFile.Exists || !dataFile.Exists) return null;

            int blobPosition, blobLength;
            var indexPosition = GetChunkIndexPosition(chunkPos);
            lock (_indexLock)
            {
                var table = GetIndexData(region, indexFile);
                if (table == null) return null;
                blobPosition = BitConverter.ToInt32(table, indexPosition);
                blobLength = BitConverter.ToInt32(table, indexPosition + sizeof(int));
            }

            if (blobPosition == IndexFileNull || blobLength == IndexFileNull) return null;

            lock (_dataLock)
            {
                using (var reader = new BinaryReader(dataFile.OpenRead()))
                {
                    reader.BaseStream.Seek(blobPosition, SeekOrigin.Begin);
                    return CompressionHelper.DecompressBytes(reader.ReadBytes(blobLength));
                }
            }
        }

        private static string GetRegionFilename(Vector3D<int> region) => $"{region.X} {region.Y} {region.Z}";

        /// <summary>The region's pos/length table (length <see cref="IndexFileLength"/>), or null when the
        /// region is missing, corrupt, or written in an older format. Caller holds <see cref="_indexLock"/>.</summary>
        private byte[] GetIndexData(Vector3D<int> region, FileInfo indexFile)
        {
            for (var i = 0; i < _cachedIndexDatas.Count; i++)
            {
                if (_cachedIndexDatas[i].Item1 != region) continue;

                var hit = _cachedIndexDatas[i];
                _cachedIndexDatas.RemoveAt(i);
                _cachedIndexDatas.Add(hit);
                return hit.Item2;
            }

            if (!indexFile.Exists) return null;

            byte[] raw;
            try
            {
                raw = CompressionHelper.DecompressBytes(File.ReadAllBytes(indexFile.FullName));
            }
            catch (Exception)
            {
                return null;
            }

            if (raw.Length != HeaderLength + IndexFileLength) return null;
            if (BitConverter.ToInt32(raw, 0) != _magic || BitConverter.ToInt32(raw, sizeof(int)) != _version)
                return null;

            var table = new byte[IndexFileLength];
            Array.Copy(raw, HeaderLength, table, 0, IndexFileLength);
            CacheIndexData(region, table);
            return table;
        }

        private void WriteIndexData(Vector3D<int> region, FileInfo indexFile, byte[] table)
        {
            var tmp = new FileInfo(indexFile.FullName + TempExt);
            using (var stream = new GZipStream(tmp.Create(), CompressionMode.Compress))
            {
                stream.Write(BitConverter.GetBytes(_magic), 0, sizeof(int));
                stream.Write(BitConverter.GetBytes(_version), 0, sizeof(int));
                stream.Write(table, 0, table.Length);
            }
            tmp.MoveTo(indexFile.FullName, true);
            CacheIndexData(region, table);
        }

        private void CacheIndexData(Vector3D<int> region, byte[] table)
        {
            for (var i = 0; i < _cachedIndexDatas.Count; i++)
                if (_cachedIndexDatas[i].Item1 == region)
                {
                    _cachedIndexDatas.RemoveAt(i);
                    break;
                }

            _cachedIndexDatas.Add(new Tuple<Vector3D<int>, byte[]>(region, table));
            if (_cachedIndexDatas.Count > MaxCachedIndexDatas)
                _cachedIndexDatas.RemoveAt(0);
        }

        private static byte[] NewEmptyTable()
        {
            var table = new byte[IndexFileLength];
            var nul = BitConverter.GetBytes(IndexFileNull);
            for (var i = 0; i < IndexFileLength; i += sizeof(int))
                Array.Copy(nul, 0, table, i, sizeof(int));
            return table;
        }

        private static bool ShouldCompact(byte[] table, FileInfo dataFile)
        {
            if (!dataFile.Exists) return false;
            var fileLength = dataFile.Length;
            if (fileLength < CompactMinBytes) return false;

            long liveBytes = 0;
            for (var i = sizeof(int); i < IndexFileLength; i += sizeof(int) * 2)
            {
                var length = BitConverter.ToInt32(table, i);
                if (length != IndexFileNull) liveBytes += length;
            }

            return fileLength > liveBytes * CompactDeadRatio;
        }

        /// <summary>Rewrites a region's data file keeping only live blobs, reclaiming the dead bytes left by
        /// re-saved blobs. Each blob is an independent GZip member, copied verbatim (no decompress/recompress).
        /// Takes Data→Index — the only place both locks are held; the strictly sequential single-lock use
        /// elsewhere means this can't deadlock.</summary>
        private void Compact(Vector3D<int> region, FileInfo indexFile, FileInfo dataFile)
        {
            lock (_dataLock)
            lock (_indexLock)
            {
                var table = GetIndexData(region, indexFile);
                if (table == null || !dataFile.Exists) return;

                var newTable = NewEmptyTable();
                var tmpData = new FileInfo(dataFile.FullName + TempExt);
                var buffer = new byte[1 << 16];

                using (var src = dataFile.OpenRead())
                using (var dst = tmpData.Create())
                {
                    for (var entry = 0; entry < IndexFileLength; entry += sizeof(int) * 2)
                    {
                        var position = BitConverter.ToInt32(table, entry);
                        var length = BitConverter.ToInt32(table, entry + sizeof(int));
                        if (position == IndexFileNull || length == IndexFileNull) continue;

                        var newPosition = (int) dst.Position;
                        src.Seek(position, SeekOrigin.Begin);
                        var remaining = length;
                        while (remaining > 0)
                        {
                            var read = src.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                            if (read <= 0) break;
                            dst.Write(buffer, 0, read);
                            remaining -= read;
                        }

                        Array.Copy(BitConverter.GetBytes(newPosition), 0, newTable, entry, sizeof(int));
                        Array.Copy(BitConverter.GetBytes(length), 0, newTable, entry + sizeof(int), sizeof(int));
                    }
                }

                tmpData.MoveTo(dataFile.FullName, true);
                WriteIndexData(region, indexFile, newTable);
            }
        }

        private static int GetChunkIndexPosition(Vector3D<int> chunkPos)
        {
            var chunkInRegion = ChunkInRegion(chunkPos);
            return (ChunksInRegionSquared * chunkInRegion.X + ChunksInRegion * chunkInRegion.Y + chunkInRegion.Z) *
                   sizeof(int) * 2;
        }

        private static Vector3D<int> ChunkToRegion(Vector3D<int> v) => new Vector3D<int>(
            v.X * Chunk.Size < 0 ? (v.X * Chunk.Size + 1) / RegionSize - 1 : v.X / ChunksInRegion,
            v.Y * Chunk.Size < 0 ? (v.Y * Chunk.Size + 1) / RegionSize - 1 : v.Y / ChunksInRegion,
            v.Z * Chunk.Size < 0 ? (v.Z * Chunk.Size + 1) / RegionSize - 1 : v.Z / ChunksInRegion);

        private static Vector3D<int> ChunkInRegion(Vector3D<int> v) => new Vector3D<int>(
            v.X < 0 ? (v.X + 1) % ChunksInRegion + ChunksInRegion - 1 : v.X % ChunksInRegion,
            v.Y < 0 ? (v.Y + 1) % ChunksInRegion + ChunksInRegion - 1 : v.Y % ChunksInRegion,
            v.Z < 0 ? (v.Z + 1) % ChunksInRegion + ChunksInRegion - 1 : v.Z % ChunksInRegion);
    }
}

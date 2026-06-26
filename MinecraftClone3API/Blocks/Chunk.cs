using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Blocks
{
    public class Chunk
    {
        public const int Size = 16;
        public const float Radius = Size * 0.8660254F;      //a*sqrt(3)/2

        public readonly WorldBase World;
        public readonly Vector3i Position;

        public Vector3i Min => _min;
        public Vector3i Max => _max;

        public bool IsEmpty => _min.X == Size;

        public bool NeedsSaving;
        public DateTime Time;

        // Bit-packed paletted storage; volatile so a copy-on-grow swap (a new value entering the palette)
        // publishes to the reader threads (mesher, network serialize) atomically. Same-value writes
        // mutate in place — a benign single-entry torn read, as the old dense ushort[] already tolerated.
        // Each container has a single writer thread; see PaletteStorage.
        private volatile PaletteStorage _blockStorage;
        private volatile PaletteStorage _lightStorage;
        // Sky light (sun) level 0..15 stored raw as a ushort, in its own container so the all-sky air
        // regions stay a single palette value. Same single-writer rule as _lightStorage (server light
        // thread / client apply thread post-publish; LoadThread pre-publish during gen seeding).
        private volatile PaletteStorage _skyStorage;
        // Concurrent: the loopback clone (apply thread) copies a server chunk's block data while the
        // server tick thread may be mutating it via SetBlockData. Rare (block-data changes only), but a
        // plain Dictionary copy under concurrent mutation throws; ConcurrentDictionary enumerates safely.
        private readonly ConcurrentDictionary<Vector3iChunk, BlockData> _blockDatas;

        /// <summary>Flattens a block coordinate into the linear storage index. Disk/wire format is
        /// unaffected as long as the (de)serializers iterate x/y/z in this same order.</summary>
        internal static int Index(int x, int y, int z) => (x * Size + y) * Size + z;

        /// <summary>Inverse of <see cref="Index"/>: unpacks a storage index back to its block coordinate.</summary>
        internal static Vector3i FromIndex(int index) => new Vector3i(index / (Size * Size), index / Size % Size, index % Size);

        private Vector3i _min = new Vector3i(Size);
        private Vector3i _max = new Vector3i(-1);


        public Chunk(WorldBase world, Vector3i position)
        {
            World = world;
            Position = position;
            Time = DateTime.Now;

            _blockStorage = new PaletteStorage(0);
            _lightStorage = new PaletteStorage(0);
            _skyStorage = new PaletteStorage(0);
            _blockDatas = new ConcurrentDictionary<Vector3iChunk, BlockData>();
        }

        /// <summary>Wraps a deserialized or freshly generated <see cref="CachedChunk"/>, adopting its
        /// paletted storage directly (a reference handoff, no per-block work) so the server's Update
        /// drain — which runs on the render thread in singleplayer — does no chunk copying.</summary>
        internal Chunk(CachedChunk cachedChunk)
        {
            World = cachedChunk.World;
            Position = cachedChunk.Position;
            Time = DateTime.Now;

            _blockStorage = cachedChunk.BlockStorage;
            _lightStorage = cachedChunk.LightStorage;
            _skyStorage = cachedChunk.SkyStorage;
            _blockDatas = cachedChunk.BlockDatas;
            _min = cachedChunk.Min;
            _max = cachedChunk.Max;
        }

        /// <summary>Clones <paramref name="source"/>'s storage into a fresh chunk bound to
        /// <paramref name="world"/>. The singleplayer loopback apply path uses this to copy a streamed
        /// server chunk without any serialize/compress round trip; the paletted copy is a small palette
        /// array plus a packed index array (uniform chunks copy almost nothing). The copy tolerates the
        /// server mutating <paramref name="source"/> concurrently — a torn entry self-corrects via the
        /// next BlockChanges delta, matching the existing <see cref="Write"/> race.</summary>
        internal Chunk(WorldBase world, Chunk source)
        {
            World = world;
            Position = source.Position;
            Time = DateTime.Now;

            _blockStorage = source._blockStorage.Clone();
            _lightStorage = source._lightStorage.Clone();
            _skyStorage = source._skyStorage.Clone();
            _blockDatas = new ConcurrentDictionary<Vector3iChunk, BlockData>(source._blockDatas);
            _min = source._min;
            _max = source._max;
        }

        public void SetBlock(Vector3i blockPos, ushort id)
        {
            var index = Index(blockPos.X, blockPos.Y, blockPos.Z);
            var storage = _blockStorage;
            if (storage.Get(index) == id) return;

            NeedsSaving = true;

            _blockStorage = storage.Set(index, id);
            _blockDatas.TryRemove(blockPos, out _);

            if (blockPos.X < _min.X) _min.X = blockPos.X;
            if (blockPos.Y < _min.Y) _min.Y = blockPos.Y;
            if (blockPos.Z < _min.Z) _min.Z = blockPos.Z;
            if (blockPos.X > _max.X) _max.X = blockPos.X;
            if (blockPos.Y > _max.Y) _max.Y = blockPos.Y;
            if (blockPos.Z > _max.Z) _max.Z = blockPos.Z;
        }

        public ushort GetBlock(Vector3i blockPos)
        {
            if (blockPos.X < 0 || blockPos.X >= Size || blockPos.Y < 0 || blockPos.Y >= Size || blockPos.Z < 0 || blockPos.Z >= Size)
                return 0;

            return _blockStorage.Get(Index(blockPos.X, blockPos.Y, blockPos.Z));
        }

        public void SetBlockData(Vector3i blockPos, BlockData data)
        {
            NeedsSaving = true;
            _blockDatas[blockPos] = data;
        }

        public BlockData GetBlockData(Vector3i blockPos)
        {
            return _blockDatas.TryGetValue(blockPos, out var data) ? data : null;
        }

        /// <summary>The in-chunk positions (0..Size-1) that carry block data, for the server to find the blocks
        /// it must tick (e.g. furnaces) when a chunk loads.</summary>
        public IEnumerable<Vector3i> BlockDataPositions
        {
            get
            {
                foreach (var key in _blockDatas.Keys)
                    yield return new Vector3i(key.X, key.Y, key.Z);
            }
        }

        public void SetLightLevel(Vector3i blockPos, LightLevel lightLevel)
        {
            var index = Index(blockPos.X, blockPos.Y, blockPos.Z);
            var storage = _lightStorage;
            if (storage.Get(index) == lightLevel.Binary) return;

            NeedsSaving = true;
            _lightStorage = storage.Set(index, lightLevel.Binary);

            if (blockPos.X < _min.X) _min.X = blockPos.X;
            if (blockPos.Y < _min.Y) _min.Y = blockPos.Y;
            if (blockPos.Z < _min.Z) _min.Z = blockPos.Z;
            if (blockPos.X > _max.X) _max.X = blockPos.X;
            if (blockPos.Y > _max.Y) _max.Y = blockPos.Y;
            if (blockPos.Z > _max.Z) _max.Z = blockPos.Z;
        }

        public LightLevel GetLightLevel(Vector3i blockPos) => LightLevel.FromBinary(_lightStorage.Get(Index(blockPos.X, blockPos.Y, blockPos.Z)));

        // Unlike SetLightLevel, sky writes never expand _min/_max: sky fills air everywhere, so growing
        // the bounding box would defeat the uniform-air fast path, blow up the mesher's min..max loop, and
        // break IsEmpty. The mesher only ever samples sky at faces of existing blocks (already in bounds).
        public void SetSkyLight(Vector3i blockPos, int level)
        {
            var index = Index(blockPos.X, blockPos.Y, blockPos.Z);
            var storage = _skyStorage;
            if (storage.Get(index) == level) return;

            NeedsSaving = true;
            _skyStorage = storage.Set(index, (ushort) level);
        }

        public int GetSkyLight(Vector3i blockPos) => _skyStorage.Get(Index(blockPos.X, blockPos.Y, blockPos.Z));

        /// <summary>True iff any cell in the chunk carries sky light. Drives <see cref="Graphics.ChunkRenderData.SkyExposed"/>,
        /// which gates the sun shadow passes (skipped when no visible chunk is sky-exposed).</summary>
        public bool HasAnySkyLight() => _skyStorage.ContainsNonZero();

        public void Write(BinaryWriter writer)
        {
            writer.Write(_min.X);
            writer.Write(_min.Y);
            writer.Write(_min.Z);

            writer.Write(_max.X);
            writer.Write(_max.Y);
            writer.Write(_max.Z);

            // The block palette is written by stable registry name, not session-local id, so the chunk
            // survives blocks being added/removed/reordered. Light/sky values are raw data, written as-is.
            _blockStorage.Write(writer, WriteBlockName);
            _lightStorage.Write(writer);
            _skyStorage.Write(writer);

            writer.Write(_blockDatas.Count);
            foreach (var data in _blockDatas)
            {
                writer.Write(data.Key.Binary);
                BlockData.WriteToStream(data.Value, writer);
            }
        }

        private static void WriteBlockName(BinaryWriter writer, ushort id)
            => writer.Write(GameRegistry.GetBlock(id).RegistryKey);
    }
}

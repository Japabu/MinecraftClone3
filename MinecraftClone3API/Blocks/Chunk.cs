using System;
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

        public bool NeedsSaving;
        public DateTime Time;

        private readonly ushort[] _blockIds = new ushort[Size * Size * Size];
        private readonly LightLevel[] _lightLevels = new LightLevel[Size * Size * Size];
        private readonly Dictionary<Vector3iChunk, BlockData> _blockDatas = new Dictionary<Vector3iChunk, BlockData>();

        /// <summary>Flattens a block coordinate into the 1-D storage arrays. Flat <c>ushort[]</c>/
        /// <c>LightLevel[]</c> avoid the runtime's slow multidimensional-array allocator
        /// (<c>Array.CreateInstanceMDArray</c>), which dominated chunk construction. Disk/wire format
        /// is unaffected as long as <see cref="Write"/> and <see cref="CachedChunk"/> iterate x/y/z
        /// in this same order.</summary>
        internal static int Index(int x, int y, int z) => (x * Size + y) * Size + z;

        private Vector3i _min = new Vector3i(Size);
        private Vector3i _max = new Vector3i(-1);


        public Chunk(WorldBase world, Vector3i position)
        {
            World = world;
            Position = position;

            Time = DateTime.Now;
        }

        internal Chunk(CachedChunk cachedChunk) : this(cachedChunk.World, cachedChunk.Position)
        {
            _blockIds = cachedChunk.BlockIds;
            _lightLevels = cachedChunk.LightLevels;
            _blockDatas = cachedChunk.BlockDatas;
            _min = cachedChunk.Min;
            _max = cachedChunk.Max;
        }

        public void SetBlock(Vector3i blockPos, ushort id)
        {
            if (_blockIds[Index(blockPos.X, blockPos.Y, blockPos.Z)] == id) return;

            NeedsSaving = true;

            _blockIds[Index(blockPos.X, blockPos.Y, blockPos.Z)] = id;
            _blockDatas.Remove(blockPos);

            if (blockPos.X < _min.X) _min.X = blockPos.X;
            if (blockPos.Y < _min.Y) _min.Y = blockPos.Y;
            if (blockPos.Z < _min.Z) _min.Z = blockPos.Z;
            if (blockPos.X > _max.X) _max.X = blockPos.X;
            if (blockPos.Y > _max.Y) _max.Y = blockPos.Y;
            if (blockPos.Z > _max.Z) _max.Z = blockPos.Z;
        }

        public ushort GetBlock(Vector3i blockPos)
        {
            if (_blockIds == null || blockPos.X < 0 || blockPos.X >= Size || blockPos.Y < 0 || blockPos.Y >= Size || blockPos.Z < 0 || blockPos.Z >= Size)
                return 0;

            return _blockIds[Index(blockPos.X, blockPos.Y, blockPos.Z)];
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

        public void SetLightLevel(Vector3i blockPos, LightLevel lightLevel)
        {
            NeedsSaving = true;
            _lightLevels[Index(blockPos.X, blockPos.Y, blockPos.Z)] = lightLevel;

            if (blockPos.X < _min.X) _min.X = blockPos.X;
            if (blockPos.Y < _min.Y) _min.Y = blockPos.Y;
            if (blockPos.Z < _min.Z) _min.Z = blockPos.Z;
            if (blockPos.X > _max.X) _max.X = blockPos.X;
            if (blockPos.Y > _max.Y) _max.Y = blockPos.Y;
            if (blockPos.Z > _max.Z) _max.Z = blockPos.Z;
        }
        
        public LightLevel GetLightLevel(Vector3i blockPos) => _lightLevels[Index(blockPos.X, blockPos.Y, blockPos.Z)];

        public void Write(BinaryWriter writer)
        {
            writer.Write(_min.X);
            writer.Write(_min.Y);
            writer.Write(_min.Z);

            writer.Write(_max.X);
            writer.Write(_max.Y);
            writer.Write(_max.Z);

            for (var x = _min.X; x <= _max.X; x++)
            for (var y = _min.Y; y <= _max.Y; y++)
            for (var z = _min.Z; z <= _max.Z; z++)
            {
                writer.Write(_blockIds[Index(x, y, z)]);
                writer.Write(_lightLevels[Index(x, y, z)].Binary);
            }

            writer.Write(_blockDatas.Count);
            foreach (var data in _blockDatas)
            {
                writer.Write(data.Key.Binary);
                BlockData.WriteToStream(data.Value, writer);
            }
        }
    }
}
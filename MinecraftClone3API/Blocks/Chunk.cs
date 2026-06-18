using System;
using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Blocks
{
    public class Chunk : IDisposable
    {
        public const int Size = 16;
        public const float Radius = Size * 0.8660254F;      //a*sqrt(3)/2

        public Vector3 Middle => (Position * Size + new Vector3i(Size / 2)).ToVector3();
        public bool HasTransparency => _transparentVao.UploadedCount > 0;

        public readonly WorldServer World;
        public readonly Vector3i Position;

        public bool Updated;
        public bool Uploaded;
        public bool NeedsSaving;
        public DateTime Time;

        private readonly ushort[,,] _blockIds = new ushort[Size, Size, Size];
        private readonly LightLevel[,,] _lightLevels = new LightLevel[Size, Size, Size];
        private readonly Dictionary<Vector3iChunk, BlockData> _blockDatas = new Dictionary<Vector3iChunk, BlockData>();

        private Vector3i _min = new Vector3i(Size);
        private Vector3i _max = new Vector3i(-1);

        private readonly VertexArrayObject _vao = new VertexArrayObject();
        private readonly SortedVertexArrayObject _transparentVao = new SortedVertexArrayObject();


        public Chunk(WorldServer world, Vector3i position)
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
            if (_blockIds[blockPos.X, blockPos.Y, blockPos.Z] == id) return;

            NeedsSaving = true;

            _blockIds[blockPos.X, blockPos.Y, blockPos.Z] = id;
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

            return _blockIds[blockPos.X, blockPos.Y, blockPos.Z];
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
            _lightLevels[blockPos.X, blockPos.Y, blockPos.Z] = lightLevel;

            if (blockPos.X < _min.X) _min.X = blockPos.X;
            if (blockPos.Y < _min.Y) _min.Y = blockPos.Y;
            if (blockPos.Z < _min.Z) _min.Z = blockPos.Z;
            if (blockPos.X > _max.X) _max.X = blockPos.X;
            if (blockPos.Y > _max.Y) _max.Y = blockPos.Y;
            if (blockPos.Z > _max.Z) _max.Z = blockPos.Z;
        }
        
        public LightLevel GetLightLevel(Vector3i blockPos) => _lightLevels[blockPos.X, blockPos.Y, blockPos.Z];

        public void Update()
        {
            lock (_vao)
            lock (_transparentVao)
                AddBlocksToVao();

            Updated = true;
        }

        public void Upload()
        {
            lock (_vao)
            lock (_transparentVao)
            {
                _vao.Upload();
                _vao.Clear();

                _transparentVao.Upload();
                _transparentVao.Clear();
            }
            Uploaded = true;
        }

        public void Draw() => _vao.Draw();
        public void DrawTransparent() => _transparentVao.Draw();

        public void SortTransparentFaces() => _transparentVao.Sort();

        public void Dispose()
        {
            lock (_vao)
            lock (_transparentVao)
            {
                _vao.Dispose();
                _transparentVao.Dispose();
            }
        }

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
                writer.Write(_blockIds[x, y, z]);
                writer.Write(_lightLevels[x, y, z].Binary);
            }

            writer.Write(_blockDatas.Count);
            foreach (var data in _blockDatas)
            {
                writer.Write(data.Key.Binary);
                BlockData.WriteToStream(data.Value, writer);
            }
        }

        private void AddBlocksToVao()
        {
            for (var x = _min.X; x <= _max.X; x++)
            for (var y = _min.Y; y <= _max.Y; y++)
            for (var z = _min.Z; z <= _max.Z; z++)
                if (_blockIds[x, y, z] != 0) //Remove GetBlock overhead of Air
                    ChunkMesher.AddBlockToVao(World, Position * Size + new Vector3i(x, y, z), x, y, z,
                        GameRegistry.BlockRegistry[_blockIds[x, y, z]], _vao, _transparentVao);
        }
    }
}
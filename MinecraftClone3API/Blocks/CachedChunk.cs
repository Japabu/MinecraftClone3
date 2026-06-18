using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Blocks
{
    internal class CachedChunk
    {
        public readonly WorldBase World;
        public readonly Vector3i Position;
        public readonly ushort[] BlockIds = new ushort[Chunk.Size * Chunk.Size * Chunk.Size];
        public readonly LightLevel[] LightLevels = new LightLevel[Chunk.Size * Chunk.Size * Chunk.Size];
        public readonly Dictionary<Vector3iChunk, BlockData> BlockDatas = new Dictionary<Vector3iChunk, BlockData>();

        public bool IsEmpty => Min.X == Chunk.Size;

        public Vector3i Min = new Vector3i(Chunk.Size);
        public Vector3i Max = new Vector3i(-1);

        public CachedChunk(WorldBase world, Vector3i position)
        {
            World = world;
            Position = position;
        }

        public CachedChunk(WorldBase world, Vector3i position, BinaryReader reader) : this(world, position)
        {
            Min = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            Max = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

            for (var x = Min.X; x <= Max.X; x++)
            for (var y = Min.Y; y <= Max.Y; y++)
            for (var z = Min.Z; z <= Max.Z; z++)
            {
                BlockIds[Chunk.Index(x, y, z)] = reader.ReadUInt16();
                LightLevels[Chunk.Index(x, y, z)] = LightLevel.FromBinary(reader.ReadUInt16());
            }
            
            var blockDataCount = reader.ReadInt32();
            for (var i = 0; i < blockDataCount; i++)
            {
                var blockDataPos = Vector3iChunk.FromBinary(reader.ReadUInt16());
                var blockData = BlockData.ReadFromStream(reader);

                BlockDatas.Add(blockDataPos, blockData);
            }
        }

        public void SetBlock(int x, int y, int z, Block block)
        {
            if (BlockIds[Chunk.Index(x, y, z)] == block.Id) return;

            BlockIds[Chunk.Index(x, y, z)] = block.Id;

            if (x < Min.X) Min.X = x;
            if (y < Min.Y) Min.Y = y;
            if (z < Min.Z) Min.Z = z;
            if (x > Max.X) Max.X = x;
            if (y > Max.Y) Max.Y = y;
            if (z > Max.Z) Max.Z = z;
        }

        public Block GetBlock(int x, int y, int z)
        {
            if (x < Min.X || x > Max.X ||
                y < Min.Y || y > Max.Y ||
                z < Min.Z || z > Max.Z)
                return BlockRegistry.BlockAir;

            return GameRegistry.BlockRegistry[BlockIds[Chunk.Index(x, y, z)]];
        }
    }
}
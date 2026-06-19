using System.Collections.Concurrent;
using System.IO;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Blocks
{
    /// <summary>
    /// A chunk under construction — filled by terrain generation (<see cref="SetBlock"/>) or by
    /// deserialization (the reader constructor), both on a background thread, then handed to
    /// <see cref="Chunk(CachedChunk)"/> which adopts its paletted storage by reference. Keeping the build
    /// off the live <see cref="Chunk"/> type means the publish step is a cheap reference handoff.
    /// </summary>
    internal class CachedChunk
    {
        public readonly WorldBase World;
        public readonly Vector3i Position;

        // Reassigned by Set when a new value enters the palette; this is a single-owner builder (no
        // concurrent readers until it is adopted into a Chunk), so the copy-on-grow swaps are local.
        public PaletteStorage BlockStorage = new PaletteStorage(0);
        public PaletteStorage LightStorage = new PaletteStorage(0);
        public readonly ConcurrentDictionary<Vector3iChunk, BlockData> BlockDatas = new ConcurrentDictionary<Vector3iChunk, BlockData>();

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

            BlockStorage = PaletteStorage.Read(reader);
            LightStorage = PaletteStorage.Read(reader);

            var blockDataCount = reader.ReadInt32();
            for (var i = 0; i < blockDataCount; i++)
            {
                var blockDataPos = Vector3iChunk.FromBinary(reader.ReadUInt16());
                var blockData = BlockData.ReadFromStream(reader);

                BlockDatas[blockDataPos] = blockData;
            }
        }

        public void SetBlock(int x, int y, int z, Block block)
        {
            var index = Chunk.Index(x, y, z);
            if (BlockStorage.Get(index) == block.Id) return;

            BlockStorage = BlockStorage.Set(index, block.Id);

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

            return GameRegistry.BlockRegistry[BlockStorage.Get(Chunk.Index(x, y, z))];
        }
    }
}

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
    public class CachedChunk
    {
        public readonly WorldBase World;
        public readonly Vector3i Position;

        // Reassigned by Set when a new value enters the palette; this is a single-owner builder (no
        // concurrent readers until it is adopted into a Chunk), so the copy-on-grow swaps are local.
        public PaletteStorage BlockStorage = new PaletteStorage(0);
        public PaletteStorage LightStorage = new PaletteStorage(0);
        public PaletteStorage SkyStorage = new PaletteStorage(0);
        public readonly ConcurrentDictionary<Vector3iChunk, BlockData> BlockDatas = new ConcurrentDictionary<Vector3iChunk, BlockData>();

        /// <summary>Raw entity blob loaded from disk on the load thread (null if none), deserialized + spawned
        /// on the tick thread when this chunk is published — keeping all <c>WorldServer.Entities</c> mutation on
        /// the tick thread. Not part of the chunk codec; never streamed.</summary>
        public byte[] EntityBytes;

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

            // Block palette is stored by registry name; resolve each back to a runtime id, minting an inert
            // placeholder for any name whose plugin is absent so the cell survives losslessly. Light/sky are raw.
            BlockStorage = PaletteStorage.Read(reader, ReadBlockName);
            LightStorage = PaletteStorage.Read(reader);
            SkyStorage = PaletteStorage.Read(reader);

            var blockDataCount = reader.ReadInt32();
            for (var i = 0; i < blockDataCount; i++)
            {
                var blockDataPos = Vector3iChunk.FromBinary(reader.ReadUInt16());
                var blockData = BlockData.ReadFromStream(reader);

                if (blockData != null) BlockDatas[blockDataPos] = blockData;
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

        // Gen-time sky seeding. Does NOT touch Min/Max so an all-air chunk above terrain stays IsEmpty
        // (single-value sky palette, never published/streamed — the client falls back to sky 15 for
        // unloaded chunks). The surface chunk holds terrain too, so its seeded air cells do stream.
        public void SetSkyLight(int x, int y, int z, int level)
        {
            var index = Chunk.Index(x, y, z);
            if (SkyStorage.Get(index) == level) return;

            SkyStorage = SkyStorage.Set(index, (ushort) level);
        }

        private static ushort ReadBlockName(BinaryReader reader)
            => GameRegistry.BlockRegistry.GetOrRegisterUnknown(reader.ReadString());

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

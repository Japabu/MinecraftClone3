using System.IO;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Blocks
{
    /// <summary>
    /// A region's worth of packed LOD surface columns — the unit streamed, stored, and meshed for the Phase-2
    /// distant horizon (the cheap terrain rendered far past where real <see cref="Chunk"/>s are ever loaded).
    /// A region is a <see cref="RegionBlocks"/>-block XZ footprint; columns are sampled at <see cref="Stride"/>,
    /// so the store IS the heightmap at render stride (no resampling at mesh time). Each column is one packed
    /// <see cref="long"/> describing the topmost LOD surface (block id, surface Y, sky light). It is Y-agnostic
    /// (one entry spans the whole vertical column), so unlike a <see cref="Chunk"/> there is no per-Y ownership.
    /// A filled region's <see cref="Columns"/> array is IMMUTABLE once published (re-fill replaces the whole
    /// object, never mutates in place) — that's what makes the loopback by-reference clone race-free, exactly
    /// like the <see cref="Chunk"/> streaming path.
    /// </summary>
    public class LodColumn
    {
        public const int RegionBlocks = 128;          // XZ size of a region
        public const int Stride = 4;                   // one stored column per Stride×Stride blocks
        public const int CellsPerAxis = RegionBlocks / Stride;   // 32
        public const int ColumnCount = CellsPerAxis * CellsPerAxis;  // 1024
        private const int RegionShift = 7;             // log2(RegionBlocks)
        private const int RegionMask = RegionBlocks - 1;
        private const int CellShift = 2;               // log2(Stride)

        /// <summary>Region key: (regionX, 0, regionZ), regionX = wx &gt;&gt; 7. Y is always 0 (Y-agnostic).</summary>
        public Vector3i Position;

        /// <summary>Length <see cref="ColumnCount"/>, indexed by <see cref="CellIndex"/>; a 0 entry = empty
        /// column (no surface — block id 0 = air).</summary>
        public long[] Columns;

        public LodColumn() { }

        public LodColumn(Vector3i position, long[] columns)
        {
            Position = position;
            Columns = columns;
        }

        /// <summary>Clone ctor — the loopback apply path (the carried region is immutable, so the copy is safe).</summary>
        public LodColumn(LodColumn source)
        {
            Position = source.Position;
            Columns = (long[]) source.Columns.Clone();
        }

        /// <summary>Deserialize ctor — the TCP apply path. No decompression here (the packet owns GZip).</summary>
        public LodColumn(BinaryReader reader, Vector3i position)
        {
            Position = position;
            Columns = new long[ColumnCount];
            for (var i = 0; i < ColumnCount; i++) Columns[i] = reader.ReadInt64();
        }

        public void Write(BinaryWriter writer)
        {
            for (var i = 0; i < ColumnCount; i++) writer.Write(Columns[i]);
        }

        public static Vector3i RegionKey(int wx, int wz) => new Vector3i(wx >> RegionShift, 0, wz >> RegionShift);

        /// <summary>Index of the stride cell containing world (wx,wz) within its region's <see cref="Columns"/>.</summary>
        public static int CellIndex(int wx, int wz)
            => ((wx & RegionMask) >> CellShift) * CellsPerAxis + ((wz & RegionMask) >> CellShift);

        /// <summary>World X of a region+cell column's representative corner (the cell's min block).</summary>
        public static int CellWorldX(Vector3i region, int cx) => (region.X << RegionShift) + cx * Stride;
        public static int CellWorldZ(Vector3i region, int cz) => (region.Z << RegionShift) + cz * Stride;

        public static long Pack(ushort blockId, int surfaceY, int sky)
            => (uint) blockId
               | ((long) (ushort) (short) surfaceY << 16)
               | ((long) (sky & 0xF) << 32);

        public static bool IsEmpty(long packed) => (ushort) (packed & 0xFFFF) == 0;
        public static ushort BlockId(long packed) => (ushort) (packed & 0xFFFF);
        public static int SurfaceY(long packed) => (short) ((packed >> 16) & 0xFFFF);
        public static int Sky(long packed) => (int) ((packed >> 32) & 0xF);
    }
}

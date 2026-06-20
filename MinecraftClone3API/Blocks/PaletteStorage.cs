using System;
using System.IO;

namespace MinecraftClone3API.Blocks
{
    /// <summary>
    /// Bit-packed paletted storage for a chunk's <see cref="Chunk.Size"/>³ <c>ushort</c> values (block
    /// ids or packed light). A small palette of the distinct values plus a bit-packed index array makes
    /// the common cases — a uniform chunk (one value) or a near-uniform one (a handful) — a tiny
    /// fraction of the 8 KB a dense <c>ushort[4096]</c> costs. With no skylight, light is 0 across almost
    /// the whole world, so the light container is single-value (≈16 B) for nearly every chunk. This
    /// shrinks both the per-chunk clone on the client apply path and the resident chunk heap — the
    /// allocation/GC pressure that degraded movement the longer the player travelled.
    ///
    /// <para>Thread-safety: a published instance's <see cref="_palette"/> and <see cref="_bitsPerEntry"/>
    /// are immutable. <see cref="Set"/> that reuses an existing palette value writes one packed entry in
    /// place (a benign single-value torn read for a concurrent reader — exactly what the old dense
    /// <c>ushort[]</c> already tolerated, self-corrected by the next <c>BlockChanges</c> delta); a
    /// <see cref="Set"/> that introduces a new value returns a NEW instance the caller publishes through
    /// a <c>volatile</c> field, so a reader always sees a structurally consistent snapshot. The chunk's
    /// block container is written by a single thread (server tick thread / client apply thread) and its
    /// light container by a single thread (server light thread / client apply thread); multiple threads
    /// only ever read.</para>
    /// </summary>
    public sealed class PaletteStorage
    {
        public const int Capacity = Chunk.Size * Chunk.Size * Chunk.Size;

        private readonly ushort[] _palette;
        private readonly int _paletteCount;
        private readonly int _bitsPerEntry;
        private readonly int _entriesPerLong;
        private readonly long _mask;
        private readonly long[] _data;

        /// <summary>A uniform container: every entry is <paramref name="value"/>, no index array.</summary>
        public PaletteStorage(ushort value) : this(new[] {value}, 1, 0, null) { }

        private PaletteStorage(ushort[] palette, int paletteCount, int bitsPerEntry, long[] data)
        {
            _palette = palette;
            _paletteCount = paletteCount;
            _bitsPerEntry = bitsPerEntry;
            _entriesPerLong = bitsPerEntry == 0 ? 0 : 64 / bitsPerEntry;
            _mask = bitsPerEntry == 0 ? 0 : (1L << bitsPerEntry) - 1;
            _data = data;
        }

        public ushort Get(int index)
        {
            if (_bitsPerEntry == 0) return _palette[0];

            var word = _data[index / _entriesPerLong];
            var offset = index % _entriesPerLong * _bitsPerEntry;
            return _palette[(int) ((word >>> offset) & _mask)];
        }

        /// <summary>Stores <paramref name="value"/> at <paramref name="index"/>. Returns <c>this</c> when
        /// the value already exists in the palette (the entry is rewritten in place); otherwise returns a
        /// new, wider instance the caller must publish.</summary>
        public PaletteStorage Set(int index, ushort value)
        {
            var paletteIndex = IndexOf(value);
            if (paletteIndex >= 0)
            {
                if (_bitsPerEntry != 0) WriteEntry(_data, index, paletteIndex);
                return this;
            }

            return Grow(index, value);
        }

        /// <summary>Snapshots this container into an independently mutable copy (used by the singleplayer
        /// loopback chunk clone). Reads the immutable palette and the index array; a concurrent in-place
        /// <see cref="Set"/> on the source yields at most a single torn entry, self-corrected by the next
        /// delta — the same race the old dense-array clone already tolerated.</summary>
        public PaletteStorage Clone()
        {
            var palette = new ushort[_paletteCount];
            Array.Copy(_palette, palette, _paletteCount);
            var data = _data == null ? null : (long[]) _data.Clone();
            return new PaletteStorage(palette, _paletteCount, _bitsPerEntry, data);
        }

        private PaletteStorage Grow(int index, ushort value)
        {
            var newCount = _paletteCount + 1;
            var newPalette = new ushort[newCount];
            Array.Copy(_palette, newPalette, _paletteCount);
            newPalette[_paletteCount] = value;

            var newBits = BitsFor(newCount);
            long[] newData;
            if (newBits == _bitsPerEntry)
            {
                newData = (long[]) _data.Clone();
            }
            else
            {
                var entriesPerLong = 64 / newBits;
                var mask = (1L << newBits) - 1;
                newData = new long[(Capacity + entriesPerLong - 1) / entriesPerLong];
                if (_bitsPerEntry != 0)
                    for (var i = 0; i < Capacity; i++)
                        WriteEntry(newData, i, newBits, entriesPerLong, mask, GetRawIndex(i));
            }

            var grown = new PaletteStorage(newPalette, newCount, newBits, newData);
            grown.WriteEntry(newData, index, _paletteCount);
            return grown;
        }

        /// <summary>True iff any entry is non-zero — a palette scan (O(palette), so O(1) for the common
        /// single-value-0 container, e.g. an underground chunk's all-zero sky light).</summary>
        public bool ContainsNonZero()
        {
            for (var i = 0; i < _paletteCount; i++)
                if (_palette[i] != 0)
                    return true;
            return false;
        }

        private int IndexOf(ushort value)
        {
            for (var i = 0; i < _paletteCount; i++)
                if (_palette[i] == value)
                    return i;
            return -1;
        }

        private int GetRawIndex(int index)
        {
            var word = _data[index / _entriesPerLong];
            var offset = index % _entriesPerLong * _bitsPerEntry;
            return (int) ((word >>> offset) & _mask);
        }

        private void WriteEntry(long[] data, int index, int paletteIndex)
            => WriteEntry(data, index, _bitsPerEntry, _entriesPerLong, _mask, paletteIndex);

        private static void WriteEntry(long[] data, int index, int bits, int entriesPerLong, long mask, int paletteIndex)
        {
            var word = index / entriesPerLong;
            var offset = index % entriesPerLong * bits;
            data[word] = (data[word] & ~(mask << offset)) | ((long) paletteIndex << offset);
        }

        private static int BitsFor(int count)
        {
            if (count <= 1) return 0;
            var bits = 0;
            var n = count - 1;
            while (n > 0)
            {
                bits++;
                n >>= 1;
            }
            return bits;
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write((ushort) _paletteCount);
            for (var i = 0; i < _paletteCount; i++)
                writer.Write(_palette[i]);

            writer.Write((byte) _bitsPerEntry);
            if (_bitsPerEntry == 0) return;

            writer.Write(_data.Length);
            for (var i = 0; i < _data.Length; i++)
                writer.Write(_data[i]);
        }

        public static PaletteStorage Read(BinaryReader reader)
        {
            int count = reader.ReadUInt16();
            if (count < 1 || count > Capacity)
                throw new InvalidDataException($"PaletteStorage palette count {count} out of range");

            var palette = new ushort[count];
            for (var i = 0; i < count; i++)
                palette[i] = reader.ReadUInt16();

            int bits = reader.ReadByte();
            if (bits == 0)
                return new PaletteStorage(palette, count, 0, null);
            if (bits > 16)
                throw new InvalidDataException($"PaletteStorage bits-per-entry {bits} out of range");

            var length = reader.ReadInt32();
            var entriesPerLong = 64 / bits;
            var expected = (Capacity + entriesPerLong - 1) / entriesPerLong;
            if (length != expected)
                throw new InvalidDataException($"PaletteStorage data length {length} != expected {expected}");

            var data = new long[length];
            for (var i = 0; i < length; i++)
                data[i] = reader.ReadInt64();

            return new PaletteStorage(palette, count, bits, data);
        }
    }
}

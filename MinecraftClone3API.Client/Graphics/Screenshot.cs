using System;
using System.IO;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Captures the default framebuffer to a PNG. Used by the benchmark flythrough to snapshot a few frames so
    /// a rendering change can be verified visually (not just by FPS). Dependency-free: a minimal PNG encoder
    /// (8-bit RGBA, uncompressed "stored" deflate blocks), since StbImageSharp is decode-only and we don't want
    /// to pull in an image-write package for a debug feature.
    /// </summary>
    public static class Screenshot
    {
        /// <summary>Reads the back buffer (main-thread GL) into a raw RGBA byte buffer (bottom-up, as
        /// glReadPixels returns it). Used both to write a PNG and to diff two captures.</summary>
        public static byte[] ReadBackBuffer(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadBuffer(ReadBufferMode.Back);
            GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            return pixels;
        }

        /// <summary>Reads the back buffer and writes it to <paramref name="path"/> as PNG.</summary>
        public static void CaptureBackBuffer(string path, int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            WritePng(path, width, height, ReadBackBuffer(width, height));
        }

        /// <summary>Writes a bottom-up RGBA buffer (e.g. from <see cref="ReadBackBuffer"/>) to a PNG.</summary>
        public static void WritePng(string path, int width, int height, byte[] bottomUpRgba)
        {
            // glReadPixels rows are bottom-to-top; PNG rows are top-to-bottom. Build the raw image with a
            // per-row filter byte (0 = none), flipping vertically.
            var stride = width * 4;
            var raw = new byte[height * (stride + 1)];
            for (var y = 0; y < height; y++)
            {
                var src = (height - 1 - y) * stride;
                var dst = y * (stride + 1);
                raw[dst] = 0; // filter: none
                System.Buffer.BlockCopy(bottomUpRgba, src, raw, dst + 1, stride);
            }

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            WritePng(fs, width, height, raw);
        }

        /// <summary>Writes an amplified per-pixel difference of two captures to a PNG: |a-b| × amplify on a near
        /// black background, so any rendering change (a black face, a moved silhouette, a colour/lighting shift)
        /// lights up brightly. The biggest honest "what did this change actually do" tool.</summary>
        public static void WriteDiff(string path, int width, int height, byte[] a, byte[] b, float amplify = 6f)
        {
            var diff = new byte[a.Length];
            for (var i = 0; i < a.Length; i += 4)
            {
                for (var c = 0; c < 3; c++)
                {
                    var d = Math.Abs(a[i + c] - b[i + c]) * amplify;
                    diff[i + c] = (byte) (d > 255 ? 255 : d);
                }
                diff[i + 3] = 255;
            }
            WritePng(path, width, height, diff);
        }

        private static readonly byte[] PngSignature = {137, 80, 78, 71, 13, 10, 26, 10};

        private static void WritePng(Stream s, int width, int height, byte[] rawImage)
        {
            s.Write(PngSignature, 0, PngSignature.Length);

            // IHDR
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint) width);
            WriteBE(ihdr, 4, (uint) height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // colour type RGBA
            ihdr[10] = 0; // compression
            ihdr[11] = 0; // filter
            ihdr[12] = 0; // interlace
            WriteChunk(s, "IHDR", ihdr);

            WriteChunk(s, "IDAT", ZlibStore(rawImage));
            WriteChunk(s, "IEND", Array.Empty<byte>());
        }

        /// <summary>zlib stream wrapping the data in uncompressed deflate "stored" blocks (no compression, but a
        /// valid stream every decoder accepts).</summary>
        private static byte[] ZlibStore(byte[] data)
        {
            using var ms = new MemoryStream(data.Length + data.Length / 65535 * 5 + 16);
            ms.WriteByte(0x78); // CMF
            ms.WriteByte(0x01); // FLG

            var offset = 0;
            while (offset < data.Length)
            {
                var len = Math.Min(65535, data.Length - offset);
                var final = offset + len >= data.Length;
                ms.WriteByte((byte) (final ? 1 : 0)); // BFINAL + BTYPE=00 (stored)
                ms.WriteByte((byte) (len & 0xFF));
                ms.WriteByte((byte) ((len >> 8) & 0xFF));
                var nlen = (~len) & 0xFFFF;
                ms.WriteByte((byte) (nlen & 0xFF));
                ms.WriteByte((byte) ((nlen >> 8) & 0xFF));
                ms.Write(data, offset, len);
                offset += len;
            }

            WriteBE(ms, Adler32(data));
            return ms.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, (uint) data.Length);
            s.Write(len, 0, 4);

            var typeBytes = new byte[4];
            for (var i = 0; i < 4; i++) typeBytes[i] = (byte) type[i];
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);

            var crc = Crc32(typeBytes, data);
            var crcBytes = new byte[4];
            WriteBE(crcBytes, 0, crc);
            s.Write(crcBytes, 0, 4);
        }

        private static void WriteBE(byte[] b, int o, uint v)
        {
            b[o] = (byte) (v >> 24);
            b[o + 1] = (byte) (v >> 16);
            b[o + 2] = (byte) (v >> 8);
            b[o + 3] = (byte) v;
        }

        private static void WriteBE(Stream s, uint v)
        {
            s.WriteByte((byte) (v >> 24));
            s.WriteByte((byte) (v >> 16));
            s.WriteByte((byte) (v >> 8));
            s.WriteByte((byte) v);
        }

        private static uint Adler32(byte[] data)
        {
            const uint mod = 65521;
            uint a = 1, b = 0;
            foreach (var d in data)
            {
                a = (a + d) % mod;
                b = (b + a) % mod;
            }
            return (b << 16) | a;
        }

        private static uint[] _crcTable;

        private static uint Crc32(byte[] a, byte[] b)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    var c = n;
                    for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }

            var crc = 0xFFFFFFFFu;
            foreach (var x in a) crc = _crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            foreach (var x in b) crc = _crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}

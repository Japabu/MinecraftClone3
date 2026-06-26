using System;
using System.IO;
using MinecraftClone3API.Graphics.Rhi;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Captures the rendered scene to a PNG. Used by the benchmark flythrough and the LOD inspector to snapshot
    /// frames so a rendering change can be verified visually (not just by FPS). Dependency-free: a minimal PNG
    /// encoder (8-bit RGBA, uncompressed "stored" deflate blocks), since StbImageSharp is decode-only and we
    /// don't want an image-write package for a debug feature.
    ///
    /// <para>WebGPU has no <c>glReadPixels</c> against the presented surface (it isn't readable after present),
    /// so the source is the renderer's owned HDR scene target (<see cref="Renderer.HdrScene"/>, rgba16float):
    /// it is copied texture→buffer on a transient encoder, the buffer is mapped back, and each rgba16float
    /// texel is ACES-tonemapped + gamma-encoded on the CPU to match <c>Tonemap.wgsl</c> so the PNG looks like
    /// what is on screen. The readback is already top-down, so there is no vertical flip.</para>
    /// </summary>
    public static unsafe class Screenshot
    {
        /// <summary>Reads the HDR scene target into a top-down RGBA8 byte buffer (already tonemapped to LDR).
        /// Used both to write a PNG and to diff two captures. Returns an opaque-black buffer if the readback
        /// fails (e.g. the scene texture isn't available).</summary>
        public static byte[] ReadBackBuffer(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            var scene = Renderer.HdrScene;
            if (scene == null || width <= 0 || height <= 0)
            {
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return pixels;
            }

            // WebGPU requires the copy's bytesPerRow be a multiple of 256. The HDR scene is rgba16float = 8
            // bytes/texel, so the padded row stride is the texel-row width rounded up to 256.
            const int bytesPerTexel = 8;
            var unpaddedRow = (uint)(width * bytesPerTexel);
            var paddedRow = (unpaddedRow + 255u) & ~255u;
            var bufferSize = (ulong)paddedRow * (ulong)height;

            var readback = new GpuBuffer(bufferSize, BufferUsage.CopyDst | BufferUsage.MapRead, "screenshot-readback");
            try
            {
                CopySceneToBuffer(scene, readback, paddedRow, (uint)width, (uint)height);
                if (!MapRead(readback, bufferSize, out var src))
                {
                    for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                    return pixels;
                }
                DecodeHdr(src, pixels, width, height, (int)paddedRow);
                Gpu.Api.BufferUnmap(readback.Handle);
            }
            finally
            {
                readback.Dispose();
            }
            return pixels;
        }

        private static void CopySceneToBuffer(GpuTexture scene, GpuBuffer dst, uint paddedRow, uint width, uint height)
        {
            var encoder = GpuCommandEncoder.Create("screenshot-copy");
            var source = new ImageCopyTexture
            {
                Texture = scene.Handle,
                MipLevel = 0,
                Origin = new Origin3D(0, 0, 0),
                Aspect = TextureAspect.All,
            };
            var destination = new ImageCopyBuffer
            {
                Buffer = dst.Handle,
                Layout = new TextureDataLayout { Offset = 0, BytesPerRow = paddedRow, RowsPerImage = height },
            };
            var copySize = new Extent3D(width, height, 1);
            Gpu.Api.CommandEncoderCopyTextureToBuffer(encoder.Handle, in source, in destination, in copySize);
            encoder.SubmitImmediate("screenshot-copy");
        }

        /// <summary>Best-effort synchronous map: request the map, then pump <c>DevicePoll(wait:true)</c> until
        /// the callback fires. wgpu-native's poll drives the map callback, so this resolves on the same thread
        /// without an event loop.</summary>
        private static bool MapRead(GpuBuffer buffer, ulong size, out byte* data)
        {
            data = null;
            var done = false;
            var ok = false;
            var callback = PfnBufferMapCallback.From((status, _) =>
            {
                done = true;
                ok = status == BufferMapAsyncStatus.Success;
            });
            Gpu.Api.BufferMapAsync(buffer.Handle, MapMode.Read, 0, (nuint)size, callback, null);

            for (var spins = 0; !done && spins < 1024; spins++)
                Gpu.Native.DevicePoll(Gpu.Device, true, null);

            if (!ok) return false;
            data = (byte*)Gpu.Api.BufferGetConstMappedRange(buffer.Handle, 0, (nuint)size);
            return data != null;
        }

        /// <summary>Tonemap (Narkowicz ACES) + gamma-encode each rgba16float texel to RGBA8, mirroring
        /// <c>Tonemap.wgsl</c>, writing rows top-down (no flip) and skipping the 256-alignment row padding.</summary>
        private static void DecodeHdr(byte* src, byte[] dst, int width, int height, int paddedRow)
        {
            for (var y = 0; y < height; y++)
            {
                var rowPtr = (Half*)(src + (long)y * paddedRow);
                var dstRow = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var s = x * 4;
                    var r = (float)rowPtr[s];
                    var g = (float)rowPtr[s + 1];
                    var b = (float)rowPtr[s + 2];

                    var d = dstRow + x * 4;
                    dst[d] = Encode(r);
                    dst[d + 1] = Encode(g);
                    dst[d + 2] = Encode(b);
                    dst[d + 3] = 255;
                }
            }
        }

        private static byte Encode(float x)
        {
            const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
            var mapped = (x * (a * x + b)) / (x * (c * x + d) + e);
            mapped = mapped < 0f ? 0f : mapped > 1f ? 1f : mapped;
            var gamma = MathF.Pow(mapped, 1f / 2.2f);
            var v = (int)(gamma * 255f + 0.5f);
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }

        /// <summary>Reads the scene and writes it to <paramref name="path"/> as PNG.</summary>
        public static void CaptureBackBuffer(string path, int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            WritePng(path, width, height, ReadBackBuffer(width, height));
        }

        /// <summary>Writes a top-down RGBA buffer (e.g. from <see cref="ReadBackBuffer"/>) to a PNG.</summary>
        public static void WritePng(string path, int width, int height, byte[] topDownRgba)
        {
            // PNG rows are top-to-bottom and the readback is already top-down; build the raw image with a
            // per-row filter byte (0 = none), no flip.
            var stride = width * 4;
            var raw = new byte[height * (stride + 1)];
            for (var y = 0; y < height; y++)
            {
                var src = y * stride;
                var dst = y * (stride + 1);
                raw[dst] = 0; // filter: none
                System.Buffer.BlockCopy(topDownRgba, src, raw, dst + 1, stride);
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

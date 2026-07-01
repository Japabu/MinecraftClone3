using System;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// CPU mip-chain generation for block textures — a plain 2x2 box downsample plus the one step that matters
    /// for cutout foliage: <see cref="Dilate"/>. WebGPU has no <c>GenerateMipmap</c>, so every level is built
    /// here and uploaded by <see cref="BlockTextureUploader"/>.
    ///
    /// A cutout texture's fully-transparent holes carry black RGB. That black is invisible on its own (alpha 0
    /// → the texel is discarded), but the hardware min filter re-mixes it into every minified edge at *sample*
    /// time, darkening distant foliage. Flooding the surrounding colour outward into the holes (base level and
    /// every mip) makes the filter blend leaf-with-leaf instead. It's a no-op on textures without holes.
    /// </summary>
    public static class BlockMipChain
    {
        /// <summary>Build mip levels 1..<paramref name="mipLevels"/>-1 from RGBA8 <paramref name="basePixels"/>
        /// (level 0). Result index L-1 holds level L, sized <c>max(1,width&gt;&gt;L) x max(1,height&gt;&gt;L)</c>.</summary>
        public static byte[][] Build(byte[] basePixels, int width, int height, int mipLevels)
        {
            var result = new byte[Math.Max(0, mipLevels - 1)][];

            var prevW = width;
            var prevH = height;
            var prev = ToFloat(basePixels, prevW, prevH);

            for (var level = 1; level < mipLevels; level++)
            {
                var curW = Math.Max(1, width >> level);
                var curH = Math.Max(1, height >> level);
                var cur = Downsample(prev, prevW, prevH, curW, curH);

                result[level - 1] = Pack(cur);
                Dilate(result[level - 1], curW, curH);

                prev = cur;
                prevW = curW;
                prevH = curH;
            }

            return result;
        }

        /// <summary>Flood the RGB of fully-transparent (alpha==0) texels outward from their opaque neighbours so
        /// the hardware filter blends leaf-with-leaf instead of leaf-with-black. Alpha is left untouched — the
        /// holes stay transparent (still discarded); only their otherwise-invisible RGB changes. See the type
        /// summary for why this is needed at all.</summary>
        public static void Dilate(byte[] rgba, int w, int h)
        {
            var known = new bool[w * h];
            for (var i = 0; i < w * h; i++) known[i] = rgba[i * 4 + 3] > 0;

            for (var pass = 0; pass < 32; pass++)
            {
                var next = (bool[])known.Clone();
                var filledAny = false;
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var idx = y * w + x;
                    if (known[idx]) continue;
                    int r = 0, g = 0, b = 0, cnt = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        var ni = ny * w + nx;
                        if (!known[ni]) continue;
                        r += rgba[ni * 4]; g += rgba[ni * 4 + 1]; b += rgba[ni * 4 + 2]; cnt++;
                    }
                    if (cnt == 0) continue;
                    rgba[idx * 4] = (byte)(r / cnt);
                    rgba[idx * 4 + 1] = (byte)(g / cnt);
                    rgba[idx * 4 + 2] = (byte)(b / cnt);
                    next[idx] = true;
                    filledAny = true;
                }
                known = next;
                if (!filledAny) break;
            }
        }

        private static float[] ToFloat(byte[] px, int w, int h)
        {
            var f = new float[w * h * 4];
            for (var i = 0; i < f.Length; i++) f[i] = px[i] / 255f;
            return f;
        }

        // Plain 2x2 box downsample of RGBA. Odd source dimensions clamp the second tap to the last row/column.
        private static float[] Downsample(float[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new float[dw * dh * 4];
            for (var y = 0; y < dh; y++)
            {
                var y0 = Math.Min(y * 2, sh - 1);
                var y1 = Math.Min(y * 2 + 1, sh - 1);
                for (var x = 0; x < dw; x++)
                {
                    var x0 = Math.Min(x * 2, sw - 1);
                    var x1 = Math.Min(x * 2 + 1, sw - 1);

                    var i0 = (y0 * sw + x0) * 4;
                    var i1 = (y0 * sw + x1) * 4;
                    var i2 = (y1 * sw + x0) * 4;
                    var i3 = (y1 * sw + x1) * 4;

                    var di = (y * dw + x) * 4;
                    for (var c = 0; c < 4; c++)
                        dst[di + c] = (src[i0 + c] + src[i1 + c] + src[i2 + c] + src[i3 + c]) * 0.25f;
                }
            }
            return dst;
        }

        private static byte[] Pack(float[] img)
        {
            var bytes = new byte[img.Length];
            for (var i = 0; i < img.Length; i++) bytes[i] = ToByte(img[i]);
            return bytes;
        }

        private static byte ToByte(float v)
        {
            v = v < 0f ? 0f : (v > 1f ? 1f : v);
            return (byte)(v * 255f + 0.5f);
        }
    }
}

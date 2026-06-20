using System;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Seeded simplex noise. Each instance owns its own permutation table built by a Fisher–Yates
    /// shuffle of 0..255 from the seed, so independent noise fields (continentalness, temperature, …)
    /// are decorrelated by giving each a different seed. Output of <see cref="Generate"/> is in roughly
    /// [-1, 1].
    /// </summary>
    public class OpenSimplexNoise
    {
        private readonly byte[] _perm = new byte[512];

        public OpenSimplexNoise(long seed)
        {
            // SplitMix64-seeded shuffle so a tiny salt difference fully scrambles the table (a plain
            // new Random((int)seed) leaves adjacent seeds visibly correlated).
            var state = (ulong) seed;
            for (var i = 0; i < 256; i++) _perm[i] = (byte) i;
            for (var i = 255; i > 0; i--)
            {
                state += 0x9E3779B97F4A7C15UL;
                var z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                var j = (int) (z % (ulong) (i + 1));
                (_perm[i], _perm[j]) = (_perm[j], _perm[i]);
            }
            for (var i = 0; i < 256; i++) _perm[256 + i] = _perm[i];
        }

        public float Generate(float x)
        {
            var i0 = FastFloor(x);
            var i1 = i0 + 1;
            var x0 = x - i0;
            var x1 = x0 - 1.0f;

            var t0 = 1.0f - x0 * x0;
            t0 *= t0;
            var n0 = t0 * t0 * Grad(_perm[i0 & 0xff], x0);

            var t1 = 1.0f - x1 * x1;
            t1 *= t1;
            var n1 = t1 * t1 * Grad(_perm[i1 & 0xff], x1);

            return 0.395f * (n0 + n1);
        }

        public float Generate(float x, float y)
        {
            const float f2 = 0.366025403f;
            const float g2 = 0.211324865f;

            float n0, n1, n2;

            var s = (x + y) * f2;
            var xs = x + s;
            var ys = y + s;
            var i = FastFloor(xs);
            var j = FastFloor(ys);

            var t = (i + j) * g2;
            var x0 = x - (i - t);
            var y0 = y - (j - t);

            int i1, j1;
            if (x0 > y0)
            {
                i1 = 1;
                j1 = 0;
            }
            else
            {
                i1 = 0;
                j1 = 1;
            }

            var x1 = x0 - i1 + g2;
            var y1 = y0 - j1 + g2;
            var x2 = x0 - 1.0f + 2.0f * g2;
            var y2 = y0 - 1.0f + 2.0f * g2;

            var ii = Mod(i, 256);
            var jj = Mod(j, 256);

            var t0 = 0.5f - x0 * x0 - y0 * y0;
            if (t0 < 0.0f)
            {
                n0 = 0.0f;
            }
            else
            {
                t0 *= t0;
                n0 = t0 * t0 * Grad(_perm[ii + _perm[jj]], x0, y0);
            }

            var t1 = 0.5f - x1 * x1 - y1 * y1;
            if (t1 < 0.0f)
            {
                n1 = 0.0f;
            }
            else
            {
                t1 *= t1;
                n1 = t1 * t1 * Grad(_perm[ii + i1 + _perm[jj + j1]], x1, y1);
            }

            var t2 = 0.5f - x2 * x2 - y2 * y2;
            if (t2 < 0.0f)
            {
                n2 = 0.0f;
            }
            else
            {
                t2 *= t2;
                n2 = t2 * t2 * Grad(_perm[ii + 1 + _perm[jj + 1]], x2, y2);
            }

            return 40.0f * (n0 + n1 + n2);
        }

        public float Generate(float x, float y, float z)
        {
            const float f3 = 0.333333333f;
            const float g3 = 0.166666667f;

            float n0, n1, n2, n3;

            var s = (x + y + z) * f3;
            var xs = x + s;
            var ys = y + s;
            var zs = z + s;
            var i = FastFloor(xs);
            var j = FastFloor(ys);
            var k = FastFloor(zs);

            var t = (i + j + k) * g3;

            var x0 = x - (i - t);
            var y0 = y - (j - t);
            var z0 = z - (k - t);

            int i1, j1, k1;
            int i2, j2, k2;

            if (x0 >= y0)
            {
                if (y0 >= z0)
                {
                    i1 = 1;
                    j1 = 0;
                    k1 = 0;
                    i2 = 1;
                    j2 = 1;
                    k2 = 0;
                }
                else if (x0 >= z0)
                {
                    i1 = 1;
                    j1 = 0;
                    k1 = 0;
                    i2 = 1;
                    j2 = 0;
                    k2 = 1;
                }
                else
                {
                    i1 = 0;
                    j1 = 0;
                    k1 = 1;
                    i2 = 1;
                    j2 = 0;
                    k2 = 1;
                }
            }
            else
            {
                if (y0 < z0)
                {
                    i1 = 0;
                    j1 = 0;
                    k1 = 1;
                    i2 = 0;
                    j2 = 1;
                    k2 = 1;
                }
                else if (x0 < z0)
                {
                    i1 = 0;
                    j1 = 1;
                    k1 = 0;
                    i2 = 0;
                    j2 = 1;
                    k2 = 1;
                }
                else
                {
                    i1 = 0;
                    j1 = 1;
                    k1 = 0;
                    i2 = 1;
                    j2 = 1;
                    k2 = 0;
                }
            }

            var x1 = x0 - i1 + g3;
            var y1 = y0 - j1 + g3;
            var z1 = z0 - k1 + g3;
            var x2 = x0 - i2 + 2.0f * g3;
            var y2 = y0 - j2 + 2.0f * g3;
            var z2 = z0 - k2 + 2.0f * g3;
            var x3 = x0 - 1.0f + 3.0f * g3;
            var y3 = y0 - 1.0f + 3.0f * g3;
            var z3 = z0 - 1.0f + 3.0f * g3;

            var ii = Mod(i, 256);
            var jj = Mod(j, 256);
            var kk = Mod(k, 256);

            var t0 = 0.6f - x0 * x0 - y0 * y0 - z0 * z0;
            if (t0 < 0.0f)
            {
                n0 = 0.0f;
            }
            else
            {
                t0 *= t0;
                n0 = t0 * t0 * Grad(_perm[ii + _perm[jj + _perm[kk]]], x0, y0, z0);
            }

            var t1 = 0.6f - x1 * x1 - y1 * y1 - z1 * z1;
            if (t1 < 0.0f)
            {
                n1 = 0.0f;
            }
            else
            {
                t1 *= t1;
                n1 = t1 * t1 * Grad(_perm[ii + i1 + _perm[jj + j1 + _perm[kk + k1]]], x1, y1, z1);
            }

            var t2 = 0.6f - x2 * x2 - y2 * y2 - z2 * z2;
            if (t2 < 0.0f)
            {
                n2 = 0.0f;
            }
            else
            {
                t2 *= t2;
                n2 = t2 * t2 * Grad(_perm[ii + i2 + _perm[jj + j2 + _perm[kk + k2]]], x2, y2, z2);
            }

            var t3 = 0.6f - x3 * x3 - y3 * y3 - z3 * z3;
            if (t3 < 0.0f)
            {
                n3 = 0.0f;
            }
            else
            {
                t3 *= t3;
                n3 = t3 * t3 * Grad(_perm[ii + 1 + _perm[jj + 1 + _perm[kk + 1]]], x3, y3, z3);
            }

            return 32.0f * (n0 + n1 + n2 + n3);
        }

        private static int FastFloor(float x)
        {
            return x > 0 ? (int) x : (int) x - 1;
        }

        private static int Mod(int x, int m)
        {
            var a = x % m;
            return a < 0 ? a + m : a;
        }

        private static float Grad(int hash, float x)
        {
            var h = hash & 15;
            var grad = 1.0f + (h & 7);
            if ((h & 8) != 0) grad = -grad;
            return grad * x;
        }

        private static float Grad(int hash, float x, float y)
        {
            var h = hash & 7;
            var u = h < 4 ? x : y;
            var v = h < 4 ? y : x;
            return ((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -2.0f * v : 2.0f * v);
        }

        private static float Grad(int hash, float x, float y, float z)
        {
            var h = hash & 15;
            var u = h < 8 ? x : y;
            var v = h < 4 ? y : h == 12 || h == 14 ? x : z;
            return ((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -v : v);
        }
    }
}

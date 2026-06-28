using System;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// Projection matrices for WebGPU's clip space (z in [0,1]) with <b>reverse-Z</b>:
    /// the near plane maps to depth 1 and the far plane to depth 0. Paired with a depth buffer cleared to 0
    /// and a <see cref="Silk.NET.WebGPU.CompareFunction.Greater"/> depth test, this spreads floating-point
    /// depth precision evenly across the view distance instead of bunching it at the near plane — the big
    /// win for the far LOD horizon, where conventional depth z-fights badly.
    ///
    /// Matrices are built in a row-vector convention so they compose with the camera/view
    /// math; <see cref="MatrixConvert"/> uploads them to WGSL (which then reads the same bytes as a
    /// column-major, column-vector matrix — the transpose the convention flip requires).
    /// </summary>
    public static class Projection
    {
        /// <summary>
        /// Infinite-far reverse-Z perspective. Near→1, far(∞)→0. Right-handed, looking down −Z.
        /// Best precision for the world/LOD passes — there is no far clip to z-fight against.
        /// </summary>
        public static Matrix4 ReverseZPerspective(float fovYRadians, float aspect, float near)
        {
            var f = 1f / MathF.Tan(fovYRadians * 0.5f);
            // Row-vector layout (p' = p * M). Columns map to clip (x,y,z,w):
            //   z_clip = near,  w_clip = -z_view  ->  depth = near / -z_view  (1 at near, 0 at infinity)
            var m = new Matrix4(
                f / aspect, 0f, 0f,   0f,
                0f,         f,  0f,   0f,
                0f,         0f, 0f,  -1f,
                0f,         0f, near, 0f);
            return m;
        }

        /// <summary>Finite reverse-Z perspective. Near→1, far→0. Used where a hard far clip is wanted.</summary>
        public static Matrix4 ReverseZPerspectiveFinite(float fovYRadians, float aspect, float near, float far)
        {
            var f = 1f / MathF.Tan(fovYRadians * 0.5f);
            var nf = near / (far - near);
            var m = new Matrix4(
                f / aspect, 0f, 0f,            0f,
                0f,         f,  0f,            0f,
                0f,         0f, nf,           -1f,
                0f,         0f, far * nf,      0f);
            return m;
        }

        /// <summary>
        /// Reverse-Z orthographic projection for the shadow cascade (near→1, far→0, z in [0,1]).
        /// Right-handed, looking down −Z in light space.
        /// </summary>
        public static Matrix4 ReverseZOrtho(float left, float right, float bottom, float top, float near, float far)
        {
            var rl = 1f / (right - left);
            var tb = 1f / (top - bottom);
            var fn = 1f / (far - near);
            // z_view in [-near,-far] -> depth in [1,0]:  depth = (z_view + far) * fn  with sign handling.
            var m = new Matrix4(
                2f * rl,              0f,                   0f,             0f,
                0f,                   2f * tb,              0f,             0f,
                0f,                   0f,                   fn,             0f,
                -(right + left) * rl, -(top + bottom) * tb, far * fn,       1f);
            return m;
        }
    }
}

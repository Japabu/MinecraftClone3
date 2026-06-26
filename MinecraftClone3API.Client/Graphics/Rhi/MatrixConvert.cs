using System.Runtime.InteropServices;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>A 16-float matrix laid out for direct upload into a WGSL <c>mat4x4&lt;f32&gt;</c> uniform.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Mat4
    {
        public float M00, M01, M02, M03;
        public float M10, M11, M12, M13;
        public float M20, M21, M22, M23;
        public float M30, M31, M32, M33;
    }

    public static class MatrixConvert
    {
        /// <summary>
        /// Pack a <see cref="Matrix4"/> for a WGSL uniform. The engine matrix is row-major with a row-vector
        /// convention; WGSL reads column-major with a column-vector convention. Copying the rows straight
        /// across hands WGSL the transpose — exactly the convention flip needed — so no explicit transpose.
        /// </summary>
        public static Mat4 ToGpu(in Matrix4 m) => new Mat4
        {
            M00 = m.Row1.X, M01 = m.Row1.Y, M02 = m.Row1.Z, M03 = m.Row1.W,
            M10 = m.Row2.X, M11 = m.Row2.Y, M12 = m.Row2.Z, M13 = m.Row2.W,
            M20 = m.Row3.X, M21 = m.Row3.Y, M22 = m.Row3.Z, M23 = m.Row3.W,
            M30 = m.Row4.X, M31 = m.Row4.Y, M32 = m.Row4.Z, M33 = m.Row4.W,
        };

        public static Vector4 ToVec4(Vector3 v, float w = 0f) => new Vector4(v.X, v.Y, v.Z, w);
    }
}

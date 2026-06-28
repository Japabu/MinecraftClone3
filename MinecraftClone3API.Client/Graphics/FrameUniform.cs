using System.Runtime.InteropServices;
using MinecraftClone3API.Graphics.Rhi;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// The per-frame uniform block (bind group 0, binding 0), written once per frame and shared by every
    /// world pass — geometry, shadow resolve, composition, entities, overlays. Mirrors the WGSL
    /// <c>Frame</c> struct: two matrices then the camera position with a pad word to keep the 16-byte
    /// alignment WGSL uniforms require. The matrices are packed by <see cref="MatrixConvert.ToGpu"/>
    /// (row-major bytes read as column-major in WGSL — the reverse-Z convention flip).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FrameUniform
    {
        public Mat4 View;
        public Mat4 Proj;
        public float CameraX, CameraY, CameraZ, _pad0;

        public static FrameUniform From(in Matrix4 view, in Matrix4 proj, Vector3 cameraPos) => new FrameUniform
        {
            View = MatrixConvert.ToGpu(view),
            Proj = MatrixConvert.ToGpu(proj),
            CameraX = cameraPos.X, CameraY = cameraPos.Y, CameraZ = cameraPos.Z, _pad0 = 0f,
        };
    }
}

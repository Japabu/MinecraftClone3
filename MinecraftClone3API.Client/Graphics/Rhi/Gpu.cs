using System;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// Static facade over the active <see cref="GpuContext"/>. The exe creates the context once after the window exists and calls
    /// <see cref="Init"/>; every <see cref="Rhi"/> wrapper reads <see cref="Api"/>/<see cref="Device"/>/etc.
    /// from here. Single-threaded init, so no locking around the field.
    /// </summary>
    public static unsafe class Gpu
    {
        private static GpuContext _context;

        public static GpuContext Context =>
            _context ?? throw new InvalidOperationException("Gpu.Init has not been called");

        public static void Init(GpuContext context) => _context = context;

        public static WebGPU Api => Context.Api;
        public static Wgpu Native => Context.Native;
        public static Device* Device => Context.Device;
        public static Queue* Queue => Context.Queue;
        public static GpuFeatures Features => Context.Features;
        public static TextureFormat SurfaceFormat => Context.SurfaceFormat;

        public static void Shutdown()
        {
            _context?.Dispose();
            _context = null;
        }
    }
}

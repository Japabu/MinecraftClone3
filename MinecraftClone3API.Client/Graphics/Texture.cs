using MinecraftClone3API.Graphics.Rhi;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// A 2D RGBA8 texture for sprites, fonts, item icons, and the sky billboards. Wraps a
    /// <see cref="GpuTexture"/> and lazily caches the sprite bind group
    /// (texture + GUI sampler under <see cref="GpuLayouts.ScreenTexture"/>) so <see cref="GuiBatch"/> can bind
    /// it directly. Item-icon render targets wrap an existing <see cref="GpuTexture"/> via <see cref="FromGpu"/>.
    /// </summary>
    public sealed unsafe class Texture
    {
        public GpuTexture Gpu { get; }
        public int Width { get; }
        public int Height { get; }

        public TextureView* View => Gpu.View;

        private GpuBindGroup _guiBindGroup;
        private readonly bool _ownsGpu;

        public Texture(TextureData data)
        {
            Width = data.Width;
            Height = data.Height;
            Gpu = new GpuTexture((uint)Width, (uint)Height, TextureFormat.Rgba8Unorm,
                TextureUsage.TextureBinding | TextureUsage.CopyDst);
            Gpu.Upload<byte>(data.Pixels);
            _ownsGpu = true;
            data.Dispose();
        }

        private Texture(GpuTexture gpu, int width, int height, bool ownsGpu)
        {
            Gpu = gpu;
            Width = width;
            Height = height;
            _ownsGpu = ownsGpu;
        }

        /// <summary>Wrap an already-created GPU texture (e.g. an item-icon colour target) without taking ownership.</summary>
        public static Texture FromGpu(GpuTexture gpu) => new Texture(gpu, (int)gpu.Width, (int)gpu.Height, false);

        /// <summary>The cached sprite bind group (group 0 of the sprite pipeline): this texture + the GUI sampler.</summary>
        public GpuBindGroup GuiBindGroup => _guiBindGroup ??= new GpuBindGroup(GpuLayouts.ScreenTexture, new[]
        {
            GpuBindGroup.Texture(0, View),
            GpuBindGroup.Sampler(1, GpuSamplers.Gui),
        }, "sprite");

        public void Dispose()
        {
            _guiBindGroup?.Dispose();
            if (_ownsGpu) Gpu.Dispose();
        }
    }
}

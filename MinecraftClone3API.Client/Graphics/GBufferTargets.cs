using System;
using Silk.NET.WebGPU;
using MinecraftClone3API.Graphics.Rhi;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// The deferred G-buffer render targets.
    /// Three colour attachments — diffuse, encoded normal (+ material flag in .w), and baked light (rgb block
    /// light, a sky factor) — plus a <b>depth32float</b> depth target. Reverse-Z: the depth is cleared to 0
    /// and the geometry pipelines compare <see cref="CompareFunction.Greater"/>. All four are also
    /// <see cref="TextureUsage.TextureBinding"/> so the shadow-resolve and composition passes sample them.
    /// Recreated on framebuffer resize.
    /// </summary>
    public sealed class GBufferTargets : IDisposable
    {
        public const TextureFormat DiffuseFormat = TextureFormat.Rgba8Unorm;
        public const TextureFormat NormalFormat = TextureFormat.Rgba8Unorm;
        public const TextureFormat LightFormat = TextureFormat.Rgba8Unorm;
        public const TextureFormat DepthFormat = TextureFormat.Depth32float;

        public GpuTexture Diffuse { get; private set; }
        public GpuTexture Normal { get; private set; }
        public GpuTexture Light { get; private set; }
        public GpuTexture Depth { get; private set; }

        public int Width { get; private set; }
        public int Height { get; private set; }

        public GBufferTargets(int width, int height) => Create(width, height);

        private void Create(int width, int height)
        {
            Width = width;
            Height = height;
            const TextureUsage usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding;
            Diffuse = new GpuTexture((uint)width, (uint)height, DiffuseFormat, usage, label: "GBuffer.Diffuse");
            Normal = new GpuTexture((uint)width, (uint)height, NormalFormat, usage, label: "GBuffer.Normal");
            Light = new GpuTexture((uint)width, (uint)height, LightFormat, usage, label: "GBuffer.Light");
            Depth = new GpuTexture((uint)width, (uint)height, DepthFormat, usage, label: "GBuffer.Depth");
        }

        public void Resize(int width, int height)
        {
            if (width == Width && height == Height) return;
            DisposeTextures();
            Create(width, height);
        }

        /// <summary>Begin the geometry pass: clear the three colour targets to 0 and the reverse-Z depth to 0.</summary>
        public unsafe RenderPass BeginGeometryPass(Rhi.GpuCommandEncoder encoder)
        {
            Span<ColorAttachment> colors = stackalloc ColorAttachment[3]
            {
                ColorAttachment.ClearTo(Diffuse.View, 0, 0, 0, 0),
                ColorAttachment.ClearTo(Normal.View, 0, 0, 0, 0),
                ColorAttachment.ClearTo(Light.View, 0, 0, 0, 0),
            };
            var depth = new DepthAttachment(Depth.View, LoadOp.Clear, 0f);
            return RenderPassBuilder.Begin(encoder, colors, depth);
        }

        /// <summary>The colour-target formats a geometry/overlay pipeline writing into this G-buffer must declare.</summary>
        public static ReadOnlySpan<TextureFormat> ColorFormats =>
            new[] { DiffuseFormat, NormalFormat, LightFormat };

        private void DisposeTextures()
        {
            Diffuse?.Dispose();
            Normal?.Dispose();
            Light?.Dispose();
            Depth?.Dispose();
        }

        public void Dispose() => DisposeTextures();
    }
}

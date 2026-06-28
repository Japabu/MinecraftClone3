using System;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using WgpuTexture = Silk.NET.WebGPU.Texture;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// A GPU texture plus a cached default view. Covers 2D render targets (G-buffer, HDR, depth), the block
    /// atlas texture-arrays, and 2D sprite textures. Mip generation for sampled textures is done on the GPU
    /// by <see cref="MipGenerator"/> (compute), so a mipped texture is created with
    /// <see cref="TextureUsage.StorageBinding"/> as well.
    /// </summary>
    public sealed unsafe class GpuTexture : IDisposable
    {
        public WgpuTexture* Handle { get; }
        public TextureView* View { get; private set; }
        public TextureFormat Format { get; }
        public uint Width { get; }
        public uint Height { get; }
        public uint Layers { get; }
        public uint MipLevels { get; }

        public GpuTexture(uint width, uint height, TextureFormat format, TextureUsage usage,
            uint layers = 1, uint mipLevels = 1, uint sampleCount = 1, string label = null,
            TextureViewDimension viewDimension = TextureViewDimension.Dimension2D)
        {
            Width = width;
            Height = height;
            Format = format;
            Layers = layers;
            MipLevels = mipLevels;

            var labelPtr = label == null ? null : (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
            var desc = new TextureDescriptor
            {
                Size = new Extent3D(width, height, layers),
                MipLevelCount = mipLevels,
                SampleCount = sampleCount,
                Dimension = TextureDimension.Dimension2D,
                Format = format,
                Usage = usage,
                Label = labelPtr,
            };
            Handle = Gpu.Api.DeviceCreateTexture(Gpu.Device, in desc);
            if (labelPtr != null) SilkMarshal.Free((nint)labelPtr);
            if (Handle == null) throw new InvalidOperationException($"wgpu: failed to create texture '{label}'");

            View = CreateView(viewDimension, 0, mipLevels, 0, layers);
        }

        public TextureView* CreateView(TextureViewDimension dimension, uint baseMip, uint mipCount,
            uint baseLayer, uint layerCount)
        {
            var desc = new TextureViewDescriptor
            {
                Format = Format,
                Dimension = dimension,
                BaseMipLevel = baseMip,
                MipLevelCount = mipCount,
                BaseArrayLayer = baseLayer,
                ArrayLayerCount = layerCount,
                Aspect = TextureAspect.All,
            };
            var view = Gpu.Api.TextureCreateView(Handle, in desc);
            if (view == null) throw new InvalidOperationException("wgpu: failed to create texture view");
            return view;
        }

        /// <summary>Upload tightly-packed pixel rows into one mip level of one array layer.</summary>
        public void Upload<T>(ReadOnlySpan<T> pixels, uint mip = 0, uint layer = 0,
            uint mipWidth = 0, uint mipHeight = 0, uint bytesPerPixel = 4) where T : unmanaged
        {
            var w = mipWidth == 0 ? Width >> (int)mip : mipWidth;
            var h = mipHeight == 0 ? Height >> (int)mip : mipHeight;
            if (w == 0) w = 1;
            if (h == 0) h = 1;

            var destination = new ImageCopyTexture
            {
                Texture = Handle,
                MipLevel = mip,
                Origin = new Origin3D(0, 0, layer),
                Aspect = TextureAspect.All,
            };
            var layout = new TextureDataLayout
            {
                Offset = 0,
                BytesPerRow = w * bytesPerPixel,
                RowsPerImage = h,
            };
            var size = new Extent3D(w, h, 1);
            fixed (T* p = pixels)
                Gpu.Api.QueueWriteTexture(Gpu.Queue, in destination, p,
                    (nuint)(pixels.Length * sizeof(T)), in layout, in size);
        }

        public void Dispose()
        {
            if (View != null) { Gpu.Api.TextureViewRelease(View); View = null; }
            if (Handle != null) Gpu.Api.TextureRelease(Handle);
        }
    }
}

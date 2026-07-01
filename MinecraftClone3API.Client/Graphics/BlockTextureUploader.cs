using System;
using Silk.NET.WebGPU;
using MinecraftClone3API.Graphics.Rhi;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Client-only WebGPU side of the block texture atlas: uploads the CPU texture data accumulated by
    /// <see cref="BlockTextureManager"/> into one <c>rgba8unorm</c> array texture per size bucket
    /// (16/64/256/1024), then uploads each level of the mip chain built by <see cref="BlockMipChain"/>
    /// (hole-dilated — keeps cutout foliage from darkening as its mips are minified).
    /// The geometry pass binds the four arrays + the block sampler as bind group 2.
    /// Kept out of Core so the headless server never links it.
    /// </summary>
    public static class BlockTextureUploader
    {
        private static readonly GpuTexture[] Arrays = new GpuTexture[BlockTextureManager.Sizes.Length];

        /// <summary>The atlas array texture for size bucket <paramref name="index"/> (0..3).</summary>
        public static GpuTexture ArrayAt(int index) => Arrays[index];

        public static void Upload()
        {
            var sizes = BlockTextureManager.Sizes;
            for (var i = 0; i < sizes.Length; i++)
            {
                var datas = BlockTextureManager.DatasFor(i);
                var layerCount = (uint)Math.Max(1, datas.Count);
                var mipLevels = MipCount((uint)sizes[i]);

                Arrays[i]?.Dispose();
                var tex = new GpuTexture((uint)sizes[i], (uint)sizes[i], TextureFormat.Rgba8Unorm,
                    TextureUsage.TextureBinding | TextureUsage.CopyDst,
                    layers: layerCount, mipLevels: mipLevels, label: $"block-atlas-{sizes[i]}",
                    viewDimension: TextureViewDimension.Dimension2DArray);

                for (var j = 0; j < datas.Count; j++)
                {
                    var data = datas[j];
                    // Dilate leaf colour into the base level's transparent holes too — the linear min filter
                    // blends mip 0 as soon as there's any minification, so black holes darken edges even here.
                    // A no-op for textures without fully-transparent texels (opaque blocks, water, glass tint).
                    BlockMipChain.Dilate(data.Pixels, data.Width, data.Height);
                    tex.Upload<byte>(new ReadOnlySpan<byte>(data.Pixels), 0, (uint)j,
                        (uint)data.Width, (uint)data.Height, 4);

                    var mips = BlockMipChain.Build(data.Pixels, data.Width, data.Height, (int)mipLevels);
                    for (var level = 1; level < mipLevels; level++)
                    {
                        var mw = (uint)Math.Max(1, data.Width >> level);
                        var mh = (uint)Math.Max(1, data.Height >> level);
                        tex.Upload<byte>(new ReadOnlySpan<byte>(mips[level - 1]), (uint)level, (uint)j, mw, mh, 4);
                    }

                    data.Dispose();
                }

                Arrays[i] = tex;
            }
        }

        /// <summary>The full mip-chain length for a square texture of side <paramref name="size"/>.</summary>
        private static uint MipCount(uint size)
        {
            uint levels = 1;
            while (size > 1) { size >>= 1; levels++; }
            return levels;
        }
    }
}

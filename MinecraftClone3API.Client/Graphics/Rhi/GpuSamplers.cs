using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// The shared samplers. Built once after the device exists.
    ///
    /// <para><b>Block atlas:</b> nearest magnification (crisp pixel-art — the Minecraft look) with trilinear
    /// minification. WebGPU forbids combining nearest magnification with hardware anisotropy (anisotropy
    /// requires all-linear filters), so hardware aniso is dropped here; the mip chain plus the foliage
    /// anti-aliased alpha test carry minification quality. <b>Repeat</b> wrap, since block UVs tile per face.</para>
    /// </summary>
    public static class GpuSamplers
    {
        /// <summary>Trilinear-min / nearest-mag, repeat — the block atlas arrays.</summary>
        public static GpuSampler Block { get; private set; }

        /// <summary>Trilinear-min / nearest-mag, <b>clamp</b> — entity &amp; worn-armor sheets sampled from the same
        /// arrays. Their unwraps don't tile (and armor sheets are square-padded with transparent space), so clamp
        /// stops the wrap from bleeding the opposite edge in at minification.</summary>
        public static GpuSampler Entity { get; private set; }

        /// <summary>Nearest, no mips, clamp — sampling the G-buffer / HDR offscreen attachments.</summary>
        public static GpuSampler Framebuffer { get; private set; }

        /// <summary>Nearest, clamp — crisp GUI sprites and font glyphs.</summary>
        public static GpuSampler Gui { get; private set; }

        /// <summary>Nearest, clamp — the pixel-art sun/moon billboards.</summary>
        public static GpuSampler Celestial { get; private set; }

        /// <summary>Linear, comparison (Greater for reverse-Z) — the shadow map PCF lookups.</summary>
        public static GpuSampler ShadowCompare { get; private set; }

        public static void Load()
        {
            Block = new GpuSampler(FilterMode.Linear, FilterMode.Nearest, MipmapFilterMode.Linear,
                AddressMode.Repeat, label: "block");
            Entity = new GpuSampler(FilterMode.Linear, FilterMode.Nearest, MipmapFilterMode.Linear,
                AddressMode.ClampToEdge, label: "entity");
            Framebuffer = new GpuSampler(FilterMode.Nearest, FilterMode.Nearest, MipmapFilterMode.Nearest,
                AddressMode.ClampToEdge, label: "framebuffer");
            Gui = new GpuSampler(FilterMode.Nearest, FilterMode.Nearest, MipmapFilterMode.Nearest,
                AddressMode.ClampToEdge, label: "gui");
            Celestial = new GpuSampler(FilterMode.Nearest, FilterMode.Nearest, MipmapFilterMode.Nearest,
                AddressMode.ClampToEdge, label: "celestial");
            // Reverse-Z shadow test: a fragment is lit when its light-space depth is GREATER than the stored
            // occluder depth (near=1, far=0), so the comparison sampler uses Greater.
            ShadowCompare = new GpuSampler(FilterMode.Linear, FilterMode.Linear, MipmapFilterMode.Nearest,
                AddressMode.ClampToEdge, CompareFunction.Greater, label: "shadow");
        }
    }
}

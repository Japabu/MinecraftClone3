using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// The shared samplers. Built once after the device exists.
    ///
    /// <para><b>Block atlas:</b> two samplers the world shader picks between per fragment, because WebGPU can't
    /// put anisotropy and nearest-magnification on one sampler (aniso requires all-linear filters). <see
    /// cref="Block"/> is nearest-mag (crisp pixel-art) for the magnifying regime; <see cref="BlockAniso"/> is
    /// all-linear + 16× anisotropy for the minifying regime, keeping grazing/distant surfaces sharp instead of
    /// over-blurring to a coarse mip. <b>Repeat</b> wrap, since block UVs tile per face.</para>
    /// </summary>
    public static class GpuSamplers
    {
        /// <summary>Nearest-mag, repeat — the block atlas arrays while magnifying (texel &gt;= pixel).</summary>
        public static GpuSampler Block { get; private set; }

        /// <summary>All-linear + 16× anisotropy, repeat — the block atlas while minifying. Anisotropy needs
        /// all-linear filters, so it can't also do nearest-mag; the world shader samples this only where a texel
        /// is smaller than a pixel and uses <see cref="Block"/> (nearest) elsewhere.</summary>
        public static GpuSampler BlockAniso { get; private set; }

        /// <summary>Nearest-mag, <b>clamp</b> — entity &amp; worn-armor sheets while magnifying. Their unwraps don't
        /// tile (and armor sheets are transparent-padded), so clamp stops the opposite edge bleeding in.</summary>
        public static GpuSampler Entity { get; private set; }

        /// <summary>All-linear + 16× anisotropy, <b>clamp</b> — entity sheets while minifying (distant/grazing
        /// mobs and dropped items). Clamp, for the same non-tiling reason as <see cref="Entity"/>.</summary>
        public static GpuSampler EntityAniso { get; private set; }

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
            BlockAniso = new GpuSampler(FilterMode.Linear, FilterMode.Linear, MipmapFilterMode.Linear,
                AddressMode.Repeat, maxAnisotropy: 16, label: "blockAniso");
            Entity = new GpuSampler(FilterMode.Linear, FilterMode.Nearest, MipmapFilterMode.Linear,
                AddressMode.ClampToEdge, label: "entity");
            EntityAniso = new GpuSampler(FilterMode.Linear, FilterMode.Linear, MipmapFilterMode.Linear,
                AddressMode.ClampToEdge, maxAnisotropy: 16, label: "entityAniso");
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

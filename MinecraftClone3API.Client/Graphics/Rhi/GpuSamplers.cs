using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// The shared samplers. Built once after the device exists.
    ///
    /// <para><b>Block atlas:</b> two samplers, picked per fragment by the world shader. WebGPU forbids combining
    /// nearest magnification with hardware anisotropy (anisotropy requires all-linear filters) <i>in one
    /// sampler</i> — but anisotropy only matters under <i>minification</i>, where nearest-vs-linear is moot, so
    /// the split costs nothing visible: <see cref="Block"/> (nearest-mag, trilinear-min) keeps crisp pixel-art
    /// up close, and <see cref="BlockAniso"/> (all-linear + 16× anisotropy) sharpens distant/grazing surfaces.
    /// <b>Repeat</b> wrap, since block UVs tile per face.</para>
    /// </summary>
    public static class GpuSamplers
    {
        /// <summary>Trilinear-min / nearest-mag, repeat — crisp magnified block faces.</summary>
        public static GpuSampler Block { get; private set; }

        /// <summary>All-linear + 16× anisotropy, repeat — minified (distant/grazing) block faces.</summary>
        public static GpuSampler BlockAniso { get; private set; }

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

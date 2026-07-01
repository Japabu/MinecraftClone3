using Silk.NET.WebGPU;
using MinecraftClone3API.Graphics.Rhi;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// The bind-group layouts shared across renderers, built once after the device exists. Renderers own
    /// their own pipelines but draw against these shared group shapes so a single per-frame uniform / atlas
    /// bind group serves every world pass:
    ///   <list type="bullet">
    ///   <item><see cref="Frame"/> — group 0: the per-frame <see cref="FrameUniform"/> (view/proj/camera).</item>
    ///   <item><see cref="BlockAtlas"/> — group 2: the four block-atlas texture arrays + the nearest & anisotropic atlas samplers.</item>
    ///   <item><see cref="ScreenTexture"/> — one sampled colour texture + sampler, for fullscreen passes
    ///   (tonemap, sprite). </item>
    ///   </list>
    /// </summary>
    public static class GpuLayouts
    {
        public static GpuBindGroupLayout Frame { get; private set; }
        public static GpuBindGroupLayout BlockAtlas { get; private set; }
        public static GpuBindGroupLayout ScreenTexture { get; private set; }

        public static void Load()
        {
            Frame = new GpuBindGroupLayout(new[]
            {
                GpuBindGroupLayout.Buffer(0, ShaderStage.Vertex | ShaderStage.Fragment, BufferBindingType.Uniform),
            }, "frame");

            BlockAtlas = new GpuBindGroupLayout(new[]
            {
                GpuBindGroupLayout.Texture(0, ShaderStage.Fragment, TextureSampleType.Float, TextureViewDimension.Dimension2DArray),
                GpuBindGroupLayout.Texture(1, ShaderStage.Fragment, TextureSampleType.Float, TextureViewDimension.Dimension2DArray),
                GpuBindGroupLayout.Texture(2, ShaderStage.Fragment, TextureSampleType.Float, TextureViewDimension.Dimension2DArray),
                GpuBindGroupLayout.Texture(3, ShaderStage.Fragment, TextureSampleType.Float, TextureViewDimension.Dimension2DArray),
                GpuBindGroupLayout.Sampler(4, ShaderStage.Fragment, SamplerBindingType.Filtering),
                GpuBindGroupLayout.Sampler(5, ShaderStage.Fragment, SamplerBindingType.Filtering),
            }, "blockAtlas");

            ScreenTexture = new GpuBindGroupLayout(new[]
            {
                GpuBindGroupLayout.Texture(0, ShaderStage.Fragment, TextureSampleType.Float),
                GpuBindGroupLayout.Sampler(1, ShaderStage.Fragment, SamplerBindingType.Filtering),
            }, "screenTexture");
        }
    }
}

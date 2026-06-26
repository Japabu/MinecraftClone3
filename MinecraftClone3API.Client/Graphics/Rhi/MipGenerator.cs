using System;
using Silk.NET.WebGPU;
using MinecraftClone3API.IO;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// Compute-shader mipmap generation for texture arrays (WebGPU has no <c>GenerateMipmap</c>). Each level
    /// is box-downsampled from the one above by <c>Mipgen.compute.wgsl</c>. Built once, reused for every
    /// atlas array. The target texture must carry <see cref="TextureUsage.StorageBinding"/> and
    /// <see cref="TextureUsage.TextureBinding"/> and be an <c>rgba8unorm</c> array.
    /// </summary>
    public sealed unsafe class MipGenerator : IDisposable
    {
        private readonly GpuShaderModule _module;
        private readonly GpuBindGroupLayout _layout;
        private readonly GpuPipelineLayout _pipelineLayout;
        private readonly GpuComputePipeline _pipeline;

        public MipGenerator()
        {
            var wgsl = ResourceReader.ReadString("System/Shaders/Mipgen.compute.wgsl");
            _module = new GpuShaderModule(wgsl, "Mipgen");
            _layout = new GpuBindGroupLayout(stackalloc BindGroupLayoutEntry[]
            {
                GpuBindGroupLayout.Texture(0, ShaderStage.Compute, TextureSampleType.Float, TextureViewDimension.Dimension2DArray),
                GpuBindGroupLayout.StorageTexture(1, ShaderStage.Compute, TextureFormat.Rgba8Unorm,
                    StorageTextureAccess.WriteOnly, TextureViewDimension.Dimension2DArray),
            }, "Mipgen");
            Span<IntPtr> bgls = stackalloc IntPtr[] { GpuPipelineLayout.Ptr(_layout) };
            _pipelineLayout = new GpuPipelineLayout(bgls, label: "Mipgen");
            _pipeline = new GpuComputePipeline(_pipelineLayout, _module, "main", "Mipgen");
        }

        /// <summary>Generate mip levels 1..N-1 of <paramref name="texture"/> from level 0. Submits its own
        /// one-shot command buffer (called at resource-load time, off the per-frame path).</summary>
        public void Generate(GpuTexture texture)
        {
            if (texture.MipLevels <= 1) return;

            var encoder = GpuCommandEncoder.Create("mipgen");
            var pass = ComputePass.Begin(encoder, "mipgen");
            pass.SetPipeline(_pipeline);

            var views = new System.Collections.Generic.List<IntPtr>();
            var groups = new System.Collections.Generic.List<GpuBindGroup>();
            for (uint mip = 1; mip < texture.MipLevels; mip++)
            {
                var srcView = texture.CreateView(TextureViewDimension.Dimension2DArray, mip - 1, 1, 0, texture.Layers);
                var dstView = texture.CreateView(TextureViewDimension.Dimension2DArray, mip, 1, 0, texture.Layers);
                views.Add((IntPtr)srcView);
                views.Add((IntPtr)dstView);

                var group = new GpuBindGroup(_layout, stackalloc BindGroupEntry[]
                {
                    GpuBindGroup.Texture(0, srcView),
                    GpuBindGroup.Texture(1, dstView),
                }, $"mipgen-{mip}");
                groups.Add(group);

                pass.SetBindGroup(0, group);
                var dstW = Math.Max(1u, texture.Width >> (int)mip);
                var dstH = Math.Max(1u, texture.Height >> (int)mip);
                pass.Dispatch((dstW + 7) / 8, (dstH + 7) / 8, texture.Layers);
            }
            pass.End();
            pass.Release();

            var cmd = encoder.Finish("mipgen");
            var cmdLocal = cmd;
            Gpu.Api.QueueSubmit(Gpu.Queue, 1, &cmdLocal);
            Gpu.Api.CommandBufferRelease(cmd);
            encoder.Release();

            foreach (var g in groups) g.Dispose();
            foreach (var v in views) Gpu.Api.TextureViewRelease((TextureView*)v);
        }

        public void Dispose()
        {
            _pipeline.Dispose();
            _pipelineLayout.Dispose();
            _layout.Dispose();
            _module.Dispose();
        }
    }
}

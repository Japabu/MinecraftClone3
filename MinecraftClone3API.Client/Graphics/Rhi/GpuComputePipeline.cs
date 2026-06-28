using System;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>A baked compute pipeline. Used for GPU-driven chunk culling and compute mipmap generation.</summary>
    public sealed unsafe class GpuComputePipeline : IDisposable
    {
        public Silk.NET.WebGPU.ComputePipeline* Handle { get; }

        public GpuComputePipeline(GpuPipelineLayout layout, GpuShaderModule module, string entryPoint, string label = null)
        {
            var entryPtr = (byte*)SilkMarshal.StringToPtr(entryPoint, NativeStringEncoding.UTF8);
            var labelPtr = label == null ? null : (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
            try
            {
                var desc = new ComputePipelineDescriptor
                {
                    Layout = layout.Handle,
                    Label = labelPtr,
                    Compute = new ProgrammableStageDescriptor
                    {
                        Module = module.Handle,
                        EntryPoint = entryPtr,
                    },
                };
                Handle = Gpu.Api.DeviceCreateComputePipeline(Gpu.Device, in desc);
                if (Handle == null) throw new InvalidOperationException($"wgpu: failed to create compute pipeline '{label}'");
            }
            finally
            {
                SilkMarshal.Free((nint)entryPtr);
                if (labelPtr != null) SilkMarshal.Free((nint)labelPtr);
            }
        }

        public void Dispose()
        {
            if (Handle != null) Gpu.Api.ComputePipelineRelease(Handle);
        }
    }
}

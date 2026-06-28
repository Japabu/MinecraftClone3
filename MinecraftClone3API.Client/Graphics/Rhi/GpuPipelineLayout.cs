using System;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// The bind-group-layout + push-constant-range contract a pipeline draws under. Push-constant ranges are
    /// a wgpu-native extension chained onto the descriptor; callers only request them when
    /// <see cref="GpuFeatures.PushConstants"/> is set (otherwise the renderer uses dynamic-offset UBOs).
    /// </summary>
    public sealed unsafe class GpuPipelineLayout : IDisposable
    {
        public Silk.NET.WebGPU.PipelineLayout* Handle { get; }

        public GpuPipelineLayout(ReadOnlySpan<IntPtr> bindGroupLayouts, ShaderStage pushConstantStages = 0,
            uint pushConstantSize = 0, string label = null)
        {
            var labelPtr = label == null ? null : (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);

            // Re-pack the bind-group-layout pointers into a contiguous native array.
            var bglCount = bindGroupLayouts.Length;
            var bgls = (Silk.NET.WebGPU.BindGroupLayout**)SilkMarshal.Allocate(bglCount * sizeof(IntPtr));
            for (var i = 0; i < bglCount; i++) bgls[i] = (Silk.NET.WebGPU.BindGroupLayout*)bindGroupLayouts[i];

            var pcRange = new PushConstantRange
            {
                Stages = pushConstantStages,
                Start = 0,
                End = pushConstantSize,
            };
            var extras = new PipelineLayoutExtras
            {
                Chain = new ChainedStruct { SType = (SType)NativeSType.STypePipelineLayoutExtras },
                PushConstantRangeCount = pushConstantSize > 0 ? (nuint)1 : 0,
                PushConstantRanges = pushConstantSize > 0 ? &pcRange : null,
            };
            try
            {
                var desc = new PipelineLayoutDescriptor
                {
                    NextInChain = pushConstantSize > 0 ? (ChainedStruct*)&extras : null,
                    BindGroupLayoutCount = (nuint)bglCount,
                    BindGroupLayouts = bgls,
                    Label = labelPtr,
                };
                Handle = Gpu.Api.DeviceCreatePipelineLayout(Gpu.Device, in desc);
            }
            finally
            {
                SilkMarshal.Free((nint)bgls);
                if (labelPtr != null) SilkMarshal.Free((nint)labelPtr);
            }
            if (Handle == null) throw new InvalidOperationException($"wgpu: failed to create pipeline layout '{label}'");
        }

        public static IntPtr Ptr(GpuBindGroupLayout layout) => (IntPtr)layout.Handle;

        public void Dispose()
        {
            if (Handle != null) Gpu.Api.PipelineLayoutRelease(Handle);
        }
    }
}

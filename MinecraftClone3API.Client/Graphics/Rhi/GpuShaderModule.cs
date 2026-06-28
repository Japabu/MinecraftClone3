using System;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>A compiled WGSL shader module. One module can hold several entry points (e.g. a vertex and
    /// fragment <c>fn</c> in the same source), referenced by name when building a pipeline.</summary>
    public sealed unsafe class GpuShaderModule : IDisposable
    {
        public Silk.NET.WebGPU.ShaderModule* Handle { get; }

        public GpuShaderModule(string wgsl, string label = null)
        {
            var api = Gpu.Api;
            var codePtr = (byte*)SilkMarshal.StringToPtr(wgsl, NativeStringEncoding.UTF8);
            var labelPtr = label == null ? null : (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
            try
            {
                var wgslDesc = new ShaderModuleWGSLDescriptor
                {
                    Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor },
                    Code = codePtr,
                };
                var desc = new ShaderModuleDescriptor
                {
                    NextInChain = (ChainedStruct*)&wgslDesc,
                    Label = labelPtr,
                };
                Handle = api.DeviceCreateShaderModule(Gpu.Device, in desc);
                if (Handle == null)
                    throw new InvalidOperationException($"wgpu: failed to compile shader module '{label}'");
            }
            finally
            {
                SilkMarshal.Free((nint)codePtr);
                if (labelPtr != null) SilkMarshal.Free((nint)labelPtr);
            }
        }

        public void Dispose()
        {
            if (Handle != null) Gpu.Api.ShaderModuleRelease(Handle);
        }
    }
}

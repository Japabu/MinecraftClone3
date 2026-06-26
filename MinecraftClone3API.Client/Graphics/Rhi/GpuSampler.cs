using System;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// A texture sampler. The shadow map uses a <i>comparison</i> sampler (<see cref="CompareFunction"/> set),
    /// sampled in WGSL via <c>textureSampleCompare</c>; WebGPU has no border-colour sampler, so "outside the
    /// shadow map = lit" is handled in the shader instead of a clamp-to-border.
    /// </summary>
    public sealed unsafe class GpuSampler : IDisposable
    {
        public Sampler* Handle { get; }

        public GpuSampler(FilterMode min, FilterMode mag, MipmapFilterMode mipmap, AddressMode address,
            CompareFunction compare = CompareFunction.Undefined, uint maxAnisotropy = 1, string label = null)
        {
            var labelPtr = label == null ? null : (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
            var desc = new SamplerDescriptor
            {
                AddressModeU = address,
                AddressModeV = address,
                AddressModeW = address,
                MagFilter = mag,
                MinFilter = min,
                MipmapFilter = mipmap,
                LodMinClamp = 0,
                LodMaxClamp = 32f,
                Compare = compare,
                MaxAnisotropy = (ushort)maxAnisotropy,
                Label = labelPtr,
            };
            Handle = Gpu.Api.DeviceCreateSampler(Gpu.Device, in desc);
            if (labelPtr != null) SilkMarshal.Free((nint)labelPtr);
            if (Handle == null) throw new InvalidOperationException($"wgpu: failed to create sampler '{label}'");
        }

        public static GpuSampler Nearest(AddressMode address = AddressMode.ClampToEdge, bool mip = false)
            => new GpuSampler(FilterMode.Nearest, FilterMode.Nearest,
                mip ? MipmapFilterMode.Nearest : MipmapFilterMode.Nearest, address);

        public static GpuSampler Linear(AddressMode address = AddressMode.ClampToEdge, bool mip = true)
            => new GpuSampler(FilterMode.Linear, FilterMode.Linear,
                mip ? MipmapFilterMode.Linear : MipmapFilterMode.Nearest, address);

        public void Dispose()
        {
            if (Handle != null) Gpu.Api.SamplerRelease(Handle);
        }
    }
}

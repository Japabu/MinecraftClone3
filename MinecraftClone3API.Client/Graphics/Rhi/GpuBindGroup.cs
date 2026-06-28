using System;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// A concrete set of resources bound together for a draw/dispatch, conforming to a
    /// <see cref="GpuBindGroupLayout"/>. Cheap to rebuild, so size-dependent groups (G-buffer textures) are
    /// recreated on resize and arena groups are recreated when a storage buffer grows.
    /// </summary>
    public sealed unsafe class GpuBindGroup : IDisposable
    {
        public Silk.NET.WebGPU.BindGroup* Handle { get; }

        public GpuBindGroup(GpuBindGroupLayout layout, ReadOnlySpan<BindGroupEntry> entries, string label = null)
        {
            var labelPtr = label == null ? null : (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
            fixed (BindGroupEntry* p = entries)
            {
                var desc = new BindGroupDescriptor
                {
                    Layout = layout.Handle,
                    EntryCount = (nuint)entries.Length,
                    Entries = p,
                    Label = labelPtr,
                };
                Handle = Gpu.Api.DeviceCreateBindGroup(Gpu.Device, in desc);
            }
            if (labelPtr != null) SilkMarshal.Free((nint)labelPtr);
            if (Handle == null) throw new InvalidOperationException($"wgpu: failed to create bind group '{label}'");
        }

        public static BindGroupEntry Buffer(uint binding, GpuBuffer buffer, ulong offset = 0, ulong size = ulong.MaxValue)
            => new BindGroupEntry
            {
                Binding = binding,
                Buffer = buffer.Handle,
                Offset = offset,
                Size = size == ulong.MaxValue ? buffer.Size - offset : size,
            };

        public static BindGroupEntry Sampler(uint binding, GpuSampler sampler)
            => new BindGroupEntry { Binding = binding, Sampler = sampler.Handle };

        public static BindGroupEntry Texture(uint binding, TextureView* view)
            => new BindGroupEntry { Binding = binding, TextureView = view };

        public void Dispose()
        {
            if (Handle != null) Gpu.Api.BindGroupRelease(Handle);
        }
    }
}

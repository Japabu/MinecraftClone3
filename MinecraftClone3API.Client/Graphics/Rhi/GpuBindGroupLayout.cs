using System;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// Describes the shape of one bind group: which bindings exist, their resource kind, and which shader
    /// stages see them. Built once per group layout (per-frame UBO, per-pass UBO, texture set, storage set)
    /// and reused to create both pipelines and the matching <see cref="GpuBindGroup"/>s.
    /// </summary>
    public sealed unsafe class GpuBindGroupLayout : IDisposable
    {
        public Silk.NET.WebGPU.BindGroupLayout* Handle { get; }
        public int EntryCount { get; }

        public GpuBindGroupLayout(ReadOnlySpan<BindGroupLayoutEntry> entries, string label = null)
        {
            EntryCount = entries.Length;
            var labelPtr = label == null ? null : (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
            fixed (BindGroupLayoutEntry* p = entries)
            {
                var desc = new BindGroupLayoutDescriptor
                {
                    EntryCount = (nuint)entries.Length,
                    Entries = p,
                    Label = labelPtr,
                };
                Handle = Gpu.Api.DeviceCreateBindGroupLayout(Gpu.Device, in desc);
            }
            if (labelPtr != null) SilkMarshal.Free((nint)labelPtr);
            if (Handle == null) throw new InvalidOperationException($"wgpu: failed to create bind group layout '{label}'");
        }

        public static BindGroupLayoutEntry Buffer(uint binding, ShaderStage visibility, BufferBindingType type,
            bool dynamicOffset = false, ulong minBindingSize = 0) => new BindGroupLayoutEntry
            {
                Binding = binding,
                Visibility = visibility,
                Buffer = new BufferBindingLayout
                {
                    Type = type,
                    HasDynamicOffset = dynamicOffset,
                    MinBindingSize = minBindingSize,
                },
            };

        public static BindGroupLayoutEntry Sampler(uint binding, ShaderStage visibility, SamplerBindingType type)
            => new BindGroupLayoutEntry
            {
                Binding = binding,
                Visibility = visibility,
                Sampler = new SamplerBindingLayout { Type = type },
            };

        public static BindGroupLayoutEntry Texture(uint binding, ShaderStage visibility, TextureSampleType sampleType,
            TextureViewDimension dimension = TextureViewDimension.Dimension2D, bool multisampled = false)
            => new BindGroupLayoutEntry
            {
                Binding = binding,
                Visibility = visibility,
                Texture = new TextureBindingLayout
                {
                    SampleType = sampleType,
                    ViewDimension = dimension,
                    Multisampled = multisampled,
                },
            };

        public static BindGroupLayoutEntry StorageTexture(uint binding, ShaderStage visibility, TextureFormat format,
            StorageTextureAccess access = StorageTextureAccess.WriteOnly,
            TextureViewDimension dimension = TextureViewDimension.Dimension2D)
            => new BindGroupLayoutEntry
            {
                Binding = binding,
                Visibility = visibility,
                StorageTexture = new StorageTextureBindingLayout
                {
                    Access = access,
                    Format = format,
                    ViewDimension = dimension,
                },
            };

        public void Dispose()
        {
            if (Handle != null) Gpu.Api.BindGroupLayoutRelease(Handle);
        }
    }
}

using System;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>Thin wrapper over a WebGPU command encoder. Records render/compute passes and buffer copies,
    /// then <see cref="Finish"/>es into a command buffer for the queue.</summary>
    public readonly unsafe struct GpuCommandEncoder
    {
        public readonly Silk.NET.WebGPU.CommandEncoder* Handle;
        public GpuCommandEncoder(Silk.NET.WebGPU.CommandEncoder* handle) => Handle = handle;

        public static GpuCommandEncoder Create(string label = null)
        {
            var labelPtr = label == null ? null : (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
            var desc = new CommandEncoderDescriptor { Label = labelPtr };
            var enc = Gpu.Api.DeviceCreateCommandEncoder(Gpu.Device, in desc);
            if (labelPtr != null) SilkMarshal.Free((nint)labelPtr);
            return new GpuCommandEncoder(enc);
        }

        public void CopyBufferToBuffer(GpuBuffer src, ulong srcOffset, GpuBuffer dst, ulong dstOffset, ulong size)
            => Gpu.Api.CommandEncoderCopyBufferToBuffer(Handle, src.Handle, srcOffset, dst.Handle, dstOffset, size);

        public void PushDebugGroup(string label)
        {
            var p = (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
            Gpu.Api.CommandEncoderPushDebugGroup(Handle, p);
            SilkMarshal.Free((nint)p);
        }

        public void PopDebugGroup() => Gpu.Api.CommandEncoderPopDebugGroup(Handle);

        public CommandBuffer* Finish(string label = null)
        {
            var labelPtr = label == null ? null : (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
            var desc = new CommandBufferDescriptor { Label = labelPtr };
            var cmd = Gpu.Api.CommandEncoderFinish(Handle, in desc);
            if (labelPtr != null) SilkMarshal.Free((nint)labelPtr);
            return cmd;
        }

        /// <summary>Finish this encoder, submit its command buffer to the queue, and release both. For one-off
        /// work outside the frame encoder (e.g. the mesh arena growing/uploading its buffers between frames);
        /// wgpu's queue is thread-safe, so this is also the off-thread upload path (see docs/threading.md).</summary>
        public void SubmitImmediate(string label = null)
        {
            var cmd = Finish(label);
            Gpu.Api.QueueSubmit(Gpu.Queue, 1, &cmd);
            Gpu.Api.CommandBufferRelease(cmd);
            Release();
        }

        public void Release() => Gpu.Api.CommandEncoderRelease(Handle);
    }

    /// <summary>Wrapper over an active render-pass encoder. Created via <see cref="RenderPassBuilder"/>.</summary>
    public readonly unsafe struct RenderPass
    {
        public readonly RenderPassEncoder* Handle;
        public RenderPass(RenderPassEncoder* handle) => Handle = handle;

        public void SetPipeline(GpuRenderPipeline pipeline) => Gpu.Api.RenderPassEncoderSetPipeline(Handle, pipeline.Handle);

        public void SetBindGroup(uint index, GpuBindGroup group, ReadOnlySpan<uint> dynamicOffsets = default)
        {
            if (dynamicOffsets.Length == 0)
            {
                Gpu.Api.RenderPassEncoderSetBindGroup(Handle, index, group.Handle, 0, null);
            }
            else
            {
                fixed (uint* p = dynamicOffsets)
                    Gpu.Api.RenderPassEncoderSetBindGroup(Handle, index, group.Handle, (nuint)dynamicOffsets.Length, p);
            }
        }

        public void SetVertexBuffer(uint slot, GpuBuffer buffer, ulong offset = 0, ulong size = ulong.MaxValue)
        {
            var sz = size == ulong.MaxValue ? buffer.Size - offset : size;
            Gpu.Api.RenderPassEncoderSetVertexBuffer(Handle, slot, buffer.Handle, offset, sz);
        }

        public void SetIndexBuffer(GpuBuffer buffer, IndexFormat format, ulong offset = 0, ulong size = ulong.MaxValue)
        {
            var sz = size == ulong.MaxValue ? buffer.Size - offset : size;
            Gpu.Api.RenderPassEncoderSetIndexBuffer(Handle, buffer.Handle, format, offset, sz);
        }

        public void SetPushConstants<T>(ShaderStage stages, uint offset, in T data) where T : unmanaged
        {
            fixed (T* p = &data)
                Gpu.Native.RenderPassEncoderSetPushConstants(Handle, stages, offset, (uint)sizeof(T),
                    new ReadOnlySpan<T>(p, 1));
        }

        public void SetViewport(float x, float y, float w, float h, float minDepth = 0, float maxDepth = 1)
            => Gpu.Api.RenderPassEncoderSetViewport(Handle, x, y, w, h, minDepth, maxDepth);

        public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
            => Gpu.Api.RenderPassEncoderDraw(Handle, vertexCount, instanceCount, firstVertex, firstInstance);

        public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0,
            int baseVertex = 0, uint firstInstance = 0)
            => Gpu.Api.RenderPassEncoderDrawIndexed(Handle, indexCount, instanceCount, firstIndex, baseVertex, firstInstance);

        public void DrawIndexedIndirect(GpuBuffer indirect, ulong offset)
            => Gpu.Api.RenderPassEncoderDrawIndexedIndirect(Handle, indirect.Handle, offset);

        /// <summary>GPU-driven draw: read draw count from <paramref name="countBuffer"/>, dispatch up to maxCount draws.</summary>
        public void MultiDrawIndexedIndirectCount(GpuBuffer indirect, ulong indirectOffset,
            GpuBuffer countBuffer, ulong countOffset, uint maxCount)
            => Gpu.Native.RenderPassEncoderMultiDrawIndexedIndirectCount(Handle, indirect.Handle, indirectOffset,
                countBuffer.Handle, countOffset, maxCount);

        public void End() => Gpu.Api.RenderPassEncoderEnd(Handle);
        public void Release() => Gpu.Api.RenderPassEncoderRelease(Handle);
    }

    /// <summary>Wrapper over an active compute-pass encoder.</summary>
    public readonly unsafe struct ComputePass
    {
        public readonly ComputePassEncoder* Handle;
        public ComputePass(ComputePassEncoder* handle) => Handle = handle;

        public void SetPipeline(GpuComputePipeline pipeline)
            => Gpu.Api.ComputePassEncoderSetPipeline(Handle, pipeline.Handle);

        public void SetBindGroup(uint index, GpuBindGroup group, ReadOnlySpan<uint> dynamicOffsets = default)
        {
            if (dynamicOffsets.Length == 0)
                Gpu.Api.ComputePassEncoderSetBindGroup(Handle, index, group.Handle, 0, null);
            else
                fixed (uint* p = dynamicOffsets)
                    Gpu.Api.ComputePassEncoderSetBindGroup(Handle, index, group.Handle, (nuint)dynamicOffsets.Length, p);
        }

        public void Dispatch(uint x, uint y = 1, uint z = 1)
            => Gpu.Api.ComputePassEncoderDispatchWorkgroups(Handle, x, y, z);

        public void End() => Gpu.Api.ComputePassEncoderEnd(Handle);
        public void Release() => Gpu.Api.ComputePassEncoderRelease(Handle);

        public static ComputePass Begin(GpuCommandEncoder encoder, string label = null)
        {
            var labelPtr = label == null ? null : (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
            var desc = new ComputePassDescriptor { Label = labelPtr };
            var pass = Gpu.Api.CommandEncoderBeginComputePass(encoder.Handle, in desc);
            if (labelPtr != null) SilkMarshal.Free((nint)labelPtr);
            return new ComputePass(pass);
        }
    }
}

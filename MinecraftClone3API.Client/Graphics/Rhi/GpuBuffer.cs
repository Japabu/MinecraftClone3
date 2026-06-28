using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// A GPU buffer (vertex / index / uniform / storage / indirect). WebGPU buffers are immutable in size,
    /// so growth allocates a new buffer and copies the live range across on the GPU
    /// (<see cref="Grow"/>) — the renderer's mesh arena grows buffers in place.
    ///
    /// Buffer creation and <see cref="QueueWrite"/> are thread-safe in wgpu-native, which is what lets chunk
    /// meshes upload off the main thread (see <c>docs/threading.md</c>).
    /// </summary>
    public sealed unsafe class GpuBuffer : IDisposable
    {
        public Buffer* Handle { get; private set; }
        public ulong Size { get; private set; }
        public BufferUsage Usage { get; }

        private readonly string _label;

        public GpuBuffer(ulong size, BufferUsage usage, string label = null)
        {
            Usage = usage;
            _label = label;
            Handle = Create(size, usage, label);
            Size = size;
        }

        private static Buffer* Create(ulong size, BufferUsage usage, string label)
        {
            // WebGPU requires buffer sizes be a multiple of 4 (COPY_DST copies, mapping). Round up.
            var rounded = (size + 3UL) & ~3UL;
            if (rounded == 0) rounded = 4;
            var labelPtr = label == null ? null : (byte*)SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);
            try
            {
                var desc = new BufferDescriptor
                {
                    Size = rounded,
                    Usage = usage,
                    MappedAtCreation = false,
                    Label = labelPtr,
                };
                var handle = Gpu.Api.DeviceCreateBuffer(Gpu.Device, in desc);
                if (handle == null) throw new InvalidOperationException($"wgpu: failed to create buffer '{label}'");
                return handle;
            }
            finally
            {
                if (labelPtr != null) SilkMarshal.Free((nint)labelPtr);
            }
        }

        /// <summary>Upload bytes at <paramref name="offset"/>. Requires the buffer have <see cref="BufferUsage.CopyDst"/>.</summary>
        public void QueueWrite<T>(ReadOnlySpan<T> data, ulong offset = 0) where T : unmanaged
        {
            if (data.Length == 0) return;
            fixed (T* ptr = data)
            {
                var bytes = (nuint)(data.Length * sizeof(T));
                Gpu.Api.QueueWriteBuffer(Gpu.Queue, Handle, offset, ptr, bytes);
            }
        }

        public void QueueWriteStruct<T>(in T value, ulong offset = 0) where T : unmanaged
        {
            fixed (T* ptr = &value)
                Gpu.Api.QueueWriteBuffer(Gpu.Queue, Handle, offset, ptr, (nuint)sizeof(T));
        }

        /// <summary>
        /// Grow to at least <paramref name="minSize"/>, preserving the first <paramref name="keepBytes"/> bytes
        /// by GPU copy. Returns true if a reallocation happened (callers that cache bind groups must rebuild).
        /// The old buffer is queued for release after the copy is encoded.
        /// </summary>
        public bool Grow(GpuCommandEncoder encoder, ulong minSize, ulong keepBytes)
        {
            if (minSize <= Size) return false;
            var newSize = Size;
            while (newSize < minSize) newSize *= 2;

            var newHandle = Create(newSize, Usage, _label);
            if (keepBytes > 0)
                Gpu.Api.CommandEncoderCopyBufferToBuffer(encoder.Handle, Handle, 0, newHandle, 0, keepBytes);

            Gpu.Api.BufferRelease(Handle);
            Handle = newHandle;
            Size = newSize;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AlignedSize<T>(int count) where T : unmanaged
            => (ulong)(count * Unsafe.SizeOf<T>());

        public void Dispose()
        {
            if (Handle != null)
            {
                Gpu.Api.BufferRelease(Handle);
                Handle = null;
            }
        }
    }
}

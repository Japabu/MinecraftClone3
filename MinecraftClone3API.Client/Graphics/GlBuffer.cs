using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Buffer-upload helper shared by the vertex array objects. The first upload of a buffer uses the
    /// caller's hint (StaticDraw for chunk meshes, since most chunks never change). A <i>re-upload</i>
    /// (firstUpload == false — a re-meshed chunk) <b>orphans</b> the buffer: <c>glBufferData(target, size,
    /// IntPtr.Zero, DynamicDraw)</c> discards the old storage so the driver allocates a fresh block and
    /// keeps the old one alive only for the draw still in flight (a driver-managed buffer rename), then
    /// <c>glBufferSubData</c> fills the fresh storage. Re-specifying the <i>same</i> in-flight buffer
    /// instead (the old StaticDraw path) forced an implicit CPU↔GPU sync — the CPU stalled until the GPU
    /// finished drawing from it, which under a deep frame queue was the ~100 ms per-edit update spike while
    /// destroying. Orphaning is a commonly-implemented (not spec-guaranteed) optimization, well-supported
    /// on Mesa and macOS GL; on Mesa it also silences the "glBufferSubData on a GL_STATIC_DRAW buffer"
    /// performance warning by flipping re-uploads to DynamicDraw. Leaves <paramref name="bufferId"/> bound
    /// to <paramref name="target"/> so the caller can set up the vertex attribute pointer on first upload.
    /// </summary>
    internal static class GlBuffer
    {
        public static void UploadArray<T>(BufferTarget target, int bufferId, List<T> data, int elementSize,
            bool firstUpload, BufferUsageHint firstHint = BufferUsageHint.StaticDraw) where T : struct
        {
            var bytes = data.Count * elementSize;
            GL.BindBuffer(target, bufferId);

            if (firstUpload)
            {
                GL.BufferData(target, bytes, ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(data)),
                    firstHint);
            }
            else
            {
                GL.BufferData(target, bytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                GL.BufferSubData(target, IntPtr.Zero, (IntPtr) bytes,
                    ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(data)));
            }
        }
    }
}

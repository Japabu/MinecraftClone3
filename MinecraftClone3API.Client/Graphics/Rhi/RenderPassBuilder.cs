using System;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>One colour attachment for a render pass: the view to write, how to load it (clear or keep),
    /// and the clear colour. HDR targets clear to the sky/black colour; G-buffer targets clear to 0.</summary>
    public readonly unsafe struct ColorAttachment
    {
        public readonly TextureView* View;
        public readonly LoadOp Load;
        public readonly StoreOp Store;
        public readonly Color Clear;

        public ColorAttachment(TextureView* view, LoadOp load, Color clear = default, StoreOp store = StoreOp.Store)
        { View = view; Load = load; Clear = clear; Store = store; }

        public static ColorAttachment ClearTo(TextureView* view, double r, double g, double b, double a = 1)
            => new ColorAttachment(view, LoadOp.Clear, new Color(r, g, b, a));

        public static ColorAttachment Keep(TextureView* view)
            => new ColorAttachment(view, LoadOp.Load);
    }

    /// <summary>The depth(-stencil) attachment for a render pass. Reverse-Z passes clear to 0.</summary>
    public readonly unsafe struct DepthAttachment
    {
        public readonly TextureView* View;
        public readonly LoadOp DepthLoad;
        public readonly StoreOp DepthStore;
        public readonly float DepthClear;
        public readonly bool ReadOnly;

        public DepthAttachment(TextureView* view, LoadOp load, float clear, StoreOp store = StoreOp.Store, bool readOnly = false)
        { View = view; DepthLoad = load; DepthClear = clear; DepthStore = store; ReadOnly = readOnly; }
    }

    public static unsafe class RenderPassBuilder
    {
        /// <summary>Begin a render pass writing the given colour attachments and optional depth attachment.</summary>
        public static RenderPass Begin(GpuCommandEncoder encoder, ReadOnlySpan<ColorAttachment> colors,
            DepthAttachment? depth = null)
        {
            Span<RenderPassColorAttachment> nativeColors =
                colors.Length <= 8 ? stackalloc RenderPassColorAttachment[colors.Length] : new RenderPassColorAttachment[colors.Length];
            for (var i = 0; i < colors.Length; i++)
            {
                var c = colors[i];
                nativeColors[i] = new RenderPassColorAttachment
                {
                    View = c.View,
                    ResolveTarget = null,
                    LoadOp = c.Load,
                    StoreOp = c.Store,
                    ClearValue = c.Clear,
                    DepthSlice = ~0u,
                };
            }

            RenderPassDepthStencilAttachment depthNative = default;
            var hasDepth = depth.HasValue;
            if (hasDepth)
            {
                var d = depth.Value;
                depthNative = new RenderPassDepthStencilAttachment
                {
                    View = d.View,
                    DepthLoadOp = d.DepthLoad,
                    DepthStoreOp = d.DepthStore,
                    DepthClearValue = d.DepthClear,
                    DepthReadOnly = d.ReadOnly,
                    StencilLoadOp = LoadOp.Undefined,
                    StencilStoreOp = StoreOp.Undefined,
                    StencilReadOnly = true,
                };
            }

            fixed (RenderPassColorAttachment* colorPtr = nativeColors)
            {
                var desc = new RenderPassDescriptor
                {
                    ColorAttachmentCount = (nuint)colors.Length,
                    ColorAttachments = colors.Length == 0 ? null : colorPtr,
                    DepthStencilAttachment = hasDepth ? &depthNative : null,
                };
                var pass = Gpu.Api.CommandEncoderBeginRenderPass(encoder.Handle, in desc);
                return new RenderPass(pass);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>One vertex attribute within a buffer layout.</summary>
    public readonly struct VertexAttr
    {
        public readonly VertexFormat Format;
        public readonly ulong Offset;
        public readonly uint Location;
        public VertexAttr(uint location, VertexFormat format, ulong offset)
        { Location = location; Format = format; Offset = offset; }
    }

    /// <summary>One interleaved vertex buffer's stride + attributes.</summary>
    public readonly struct VertexBufferDesc
    {
        public readonly ulong Stride;
        public readonly VertexStepMode StepMode;
        public readonly VertexAttr[] Attributes;
        public VertexBufferDesc(ulong stride, VertexAttr[] attributes, VertexStepMode stepMode = VertexStepMode.Vertex)
        { Stride = stride; Attributes = attributes; StepMode = stepMode; }
    }

    /// <summary>One colour attachment's format, optional blend, and write mask.</summary>
    public readonly struct ColorTargetDesc
    {
        public readonly TextureFormat Format;
        public readonly BlendState? Blend;
        public readonly ColorWriteMask WriteMask;
        public ColorTargetDesc(TextureFormat format, BlendState? blend = null,
            ColorWriteMask writeMask = ColorWriteMask.All)
        { Format = format; Blend = blend; WriteMask = writeMask; }
    }

    /// <summary>Depth-stencil configuration. The renderer is reverse-Z, so opaque passes use
    /// <see cref="CompareFunction.Greater"/> and clear depth to 0 (see <see cref="Projection"/>).</summary>
    public readonly struct DepthDesc
    {
        public readonly TextureFormat Format;
        public readonly bool WriteEnabled;
        public readonly CompareFunction Compare;
        public readonly int DepthBias;
        public readonly float DepthBiasSlopeScale;
        public DepthDesc(TextureFormat format, bool writeEnabled, CompareFunction compare,
            int depthBias = 0, float depthBiasSlopeScale = 0)
        { Format = format; WriteEnabled = writeEnabled; Compare = compare; DepthBias = depthBias; DepthBiasSlopeScale = depthBiasSlopeScale; }
    }

    /// <summary>A baked render pipeline (PSO): every distinct (shader + targets + blend + depth + cull +
    /// vertex layout) combination is one cached object.</summary>
    public sealed unsafe class GpuRenderPipeline : IDisposable
    {
        public Silk.NET.WebGPU.RenderPipeline* Handle { get; }

        public GpuRenderPipeline(
            GpuPipelineLayout layout,
            GpuShaderModule module,
            string vertexEntry,
            string fragmentEntry,
            ReadOnlySpan<VertexBufferDesc> vertexBuffers,
            ReadOnlySpan<ColorTargetDesc> colorTargets,
            DepthDesc? depth = null,
            PrimitiveTopology topology = PrimitiveTopology.TriangleList,
            CullMode cullMode = CullMode.None,
            FrontFace frontFace = FrontFace.Ccw,
            uint sampleCount = 1,
            string label = null)
        {
            var alloc = new NativeAllocList();
            try
            {
                var vsEntry = (byte*)alloc.Track(SilkMarshal.StringToPtr(vertexEntry, NativeStringEncoding.UTF8));
                var labelPtr = label == null ? null
                    : (byte*)alloc.Track(SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8));

                // --- Vertex state ---
                var vbLayouts = (VertexBufferLayout*)alloc.Alloc(vertexBuffers.Length * sizeof(VertexBufferLayout));
                for (var i = 0; i < vertexBuffers.Length; i++)
                {
                    var vb = vertexBuffers[i];
                    var attrs = (VertexAttribute*)alloc.Alloc(vb.Attributes.Length * sizeof(VertexAttribute));
                    for (var j = 0; j < vb.Attributes.Length; j++)
                    {
                        var a = vb.Attributes[j];
                        attrs[j] = new VertexAttribute { Format = a.Format, Offset = a.Offset, ShaderLocation = a.Location };
                    }
                    vbLayouts[i] = new VertexBufferLayout
                    {
                        ArrayStride = vb.Stride,
                        StepMode = vb.StepMode,
                        AttributeCount = (nuint)vb.Attributes.Length,
                        Attributes = attrs,
                    };
                }
                var vertexState = new VertexState
                {
                    Module = module.Handle,
                    EntryPoint = vsEntry,
                    BufferCount = (nuint)vertexBuffers.Length,
                    Buffers = vertexBuffers.Length == 0 ? null : vbLayouts,
                };

                // --- Fragment state ---
                FragmentState* fragmentPtr = null;
                if (fragmentEntry != null && colorTargets.Length > 0)
                {
                    var fsEntry = (byte*)alloc.Track(SilkMarshal.StringToPtr(fragmentEntry, NativeStringEncoding.UTF8));
                    var targets = (ColorTargetState*)alloc.Alloc(colorTargets.Length * sizeof(ColorTargetState));
                    for (var i = 0; i < colorTargets.Length; i++)
                    {
                        var ct = colorTargets[i];
                        BlendState* blendPtr = null;
                        if (ct.Blend.HasValue)
                        {
                            blendPtr = (BlendState*)alloc.Alloc(sizeof(BlendState));
                            *blendPtr = ct.Blend.Value;
                        }
                        targets[i] = new ColorTargetState { Format = ct.Format, Blend = blendPtr, WriteMask = ct.WriteMask };
                    }
                    var fragment = (FragmentState*)alloc.Alloc(sizeof(FragmentState));
                    *fragment = new FragmentState
                    {
                        Module = module.Handle,
                        EntryPoint = fsEntry,
                        TargetCount = (nuint)colorTargets.Length,
                        Targets = targets,
                    };
                    fragmentPtr = fragment;
                }

                // --- Depth-stencil ---
                DepthStencilState* depthPtr = null;
                if (depth.HasValue)
                {
                    var d = depth.Value;
                    depthPtr = (DepthStencilState*)alloc.Alloc(sizeof(DepthStencilState));
                    *depthPtr = new DepthStencilState
                    {
                        Format = d.Format,
                        DepthWriteEnabled = d.WriteEnabled,
                        DepthCompare = d.Compare,
                        StencilFront = new StencilFaceState { Compare = CompareFunction.Always },
                        StencilBack = new StencilFaceState { Compare = CompareFunction.Always },
                        StencilReadMask = 0,
                        StencilWriteMask = 0,
                        DepthBias = d.DepthBias,
                        DepthBiasSlopeScale = d.DepthBiasSlopeScale,
                        DepthBiasClamp = 0,
                    };
                }

                var desc = new RenderPipelineDescriptor
                {
                    Layout = layout.Handle,
                    Label = labelPtr,
                    Vertex = vertexState,
                    Primitive = new PrimitiveState
                    {
                        Topology = topology,
                        StripIndexFormat = IndexFormat.Undefined,
                        FrontFace = frontFace,
                        CullMode = cullMode,
                    },
                    DepthStencil = depthPtr,
                    Multisample = new MultisampleState { Count = sampleCount, Mask = ~0u, AlphaToCoverageEnabled = false },
                    Fragment = fragmentPtr,
                };
                Handle = Gpu.Api.DeviceCreateRenderPipeline(Gpu.Device, in desc);
                if (Handle == null) throw new InvalidOperationException($"wgpu: failed to create render pipeline '{label}'");
            }
            finally
            {
                alloc.FreeAll();
            }
        }

        public void Dispose()
        {
            if (Handle != null) Gpu.Api.RenderPipelineRelease(Handle);
        }

        /// <summary>Standard straight-alpha blend (src.a, 1-src.a) for transparent geometry / GUI.</summary>
        public static BlendState AlphaBlend => new BlendState
        {
            Color = new BlendComponent { SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
            Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
        };

        /// <summary>Minecraft's inverting blend (1-dst, 1-src) so a sprite (the crosshair) stays visible against
        /// any background by inverting the pixels behind it.</summary>
        public static BlendState InvertBlend => new BlendState
        {
            Color = new BlendComponent { SrcFactor = BlendFactor.OneMinusDst, DstFactor = BlendFactor.OneMinusSrc, Operation = BlendOperation.Add },
            Alpha = new BlendComponent { SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
        };
    }

    /// <summary>Tracks native allocations made while marshalling a descriptor, freeing them in one sweep
    /// after the pipeline is created (pipeline creation deep-copies the descriptor).</summary>
    internal sealed unsafe class NativeAllocList
    {
        private readonly List<nint> _ptrs = new List<nint>();
        public void* Alloc(int bytes) { var p = SilkMarshal.Allocate(bytes); _ptrs.Add(p); return (void*)p; }
        public nint Track(nint p) { _ptrs.Add(p); return p; }
        public void FreeAll() { foreach (var p in _ptrs) SilkMarshal.Free(p); _ptrs.Clear(); }
    }
}

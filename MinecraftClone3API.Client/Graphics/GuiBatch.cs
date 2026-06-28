using System.Collections.Generic;
using System.Runtime.InteropServices;
using MinecraftClone3API.Graphics.Rhi;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Collects the frame's GUI sprite draws and flushes them in one render pass over the surface (after the
    /// world tonemap). Sprite draws are recorded into the frame encoder: <see cref="GuiRenderer"/> appends here
    /// and the frame conductor (<see cref="Renderer"/>) flushes once the surface render pass is open. Each sprite is a
    /// unit quad with its rect / uv / colour in push constants and the texture as the group-0 bind group —
    /// the "per-draw GUI = push constants" convention.
    /// </summary>
    public static unsafe class GuiBatch
    {
        /// <summary>Push-constant block matching the WGSL <c>SpritePush</c> in Sprite.wgsl. Rect is clip-space.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct SpritePush
        {
            public Vector4 Rect;
            public Vector4 UvRect;
            public Vector4 Color;
        }

        private readonly struct Entry
        {
            public readonly Texture Texture;
            public readonly SpritePush Push;
            public Entry(Texture texture, SpritePush push) { Texture = texture; Push = push; }
        }

        private static readonly List<Entry> _entries = new List<Entry>(256);

        private static GpuShaderModule _module;
        private static GpuPipelineLayout _layout;
        private static GpuRenderPipeline _pipeline;
        private static GpuRenderPipeline _invertPipeline;
        private static GpuBuffer _quadVerts;
        private static GpuBuffer _quadIndices;

        // Sprites that use Minecraft's inverting blend (the crosshair) flush in a second pass with their own
        // pipeline, so the main alpha-blend batch stays one pipeline.
        private static readonly List<Entry> _invertEntries = new List<Entry>(4);

        public static void Load(string spriteWgsl)
        {
            _module = new GpuShaderModule(spriteWgsl, "sprite");

            _layout = new GpuPipelineLayout(new[] { GpuPipelineLayout.Ptr(GpuLayouts.ScreenTexture) },
                ShaderStage.Vertex | ShaderStage.Fragment, (uint)sizeof(SpritePush), "sprite");

            var vbDesc = new VertexBufferDesc(12, new[] { new VertexAttr(0, VertexFormat.Float32x3, 0) });
            var color = new ColorTargetDesc(Gpu.SurfaceFormat, GpuRenderPipeline.AlphaBlend);
            _pipeline = new GpuRenderPipeline(_layout, _module, "vs_main", "fs_main",
                new[] { vbDesc }, stackalloc[] { color }, depth: null,
                topology: PrimitiveTopology.TriangleList, cullMode: CullMode.None, label: "sprite");

            var invertColor = new ColorTargetDesc(Gpu.SurfaceFormat, GpuRenderPipeline.InvertBlend);
            _invertPipeline = new GpuRenderPipeline(_layout, _module, "vs_main", "fs_main",
                new[] { vbDesc }, stackalloc[] { invertColor }, depth: null,
                topology: PrimitiveTopology.TriangleList, cullMode: CullMode.None, label: "spriteInvert");

            // Unit quad in NDC corners (the shader remaps it to the push-constant rect), winding {0,2,1, 1,2,3}.
            var verts = new float[] { -1, +1, 0,  +1, +1, 0,  -1, -1, 0,  +1, -1, 0 };
            _quadVerts = new GpuBuffer((ulong)(verts.Length * sizeof(float)),
                BufferUsage.Vertex | BufferUsage.CopyDst, "sprite.verts");
            _quadVerts.QueueWrite<float>(verts);

            var indices = new uint[] { 0, 2, 1, 1, 2, 3 };
            _quadIndices = new GpuBuffer((ulong)(indices.Length * sizeof(uint)),
                BufferUsage.Index | BufferUsage.CopyDst, "sprite.indices");
            _quadIndices.QueueWrite<uint>(indices);
        }

        public static void Begin()
        {
            _entries.Clear();
            _invertEntries.Clear();
        }

        public static void Add(Texture texture, Vector4 clipRect, Vector4 uvRect, Vector4 color, bool invert = false)
        {
            (invert ? _invertEntries : _entries).Add(new Entry(texture, new SpritePush
            {
                Rect = clipRect,
                UvRect = uvRect,
                Color = color,
            }));
        }

        /// <summary>Record every accumulated sprite into the open surface render pass.</summary>
        public static void Flush(RenderPass pass)
        {
            DrawList(pass, _pipeline, _entries);
            DrawList(pass, _invertPipeline, _invertEntries);
        }

        private static void DrawList(RenderPass pass, GpuRenderPipeline pipeline, List<Entry> entries)
        {
            if (entries.Count == 0) return;
            pass.SetPipeline(pipeline);
            pass.SetVertexBuffer(0, _quadVerts);
            pass.SetIndexBuffer(_quadIndices, IndexFormat.Uint32);
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                pass.SetBindGroup(0, e.Texture.GuiBindGroup);
                var push = e.Push;
                pass.SetPushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 0, in push);
                pass.DrawIndexed(6);
            }
        }
    }
}

using System;
using System.Runtime.InteropServices;
using MinecraftClone3API.Graphics.Rhi;
using MinecraftClone3API.IO;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Draws unlit wireframe boxes into the G-buffer with <c>BlockOutline.wgsl</c>: the block-selection box and
    /// the F4 chunk-grid debug overlay both render a unit cube of line segments transformed by a per-draw MVP.
    /// The cube is centred on the origin spanning [-0.5, 0.5]; callers pre-multiply the scale/translation that
    /// maps it onto the target volume. The fragment writes only diffuse + an unlit normal (w = 1), so the light
    /// attachment is masked and composition skips shading the lines.
    /// </summary>
    public static unsafe class OutlineRenderer
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct OutlinePush
        {
            public Mat4 Transform;
            public Vector4 Color;
        }

        private static readonly float[] CubeVerts =
        {
            -0.5f, -0.5f, -0.5f,  +0.5f, -0.5f, -0.5f,  -0.5f, +0.5f, -0.5f,  +0.5f, +0.5f, -0.5f,
            +0.5f, -0.5f, +0.5f,  -0.5f, -0.5f, +0.5f,  +0.5f, +0.5f, +0.5f,  -0.5f, +0.5f, +0.5f,
        };

        private static readonly uint[] CubeLines =
        {
            0, 1, 1, 3, 3, 2, 2, 0,
            4, 5, 5, 7, 7, 6, 6, 4,
            0, 5, 1, 4, 2, 7, 3, 6,
        };

        private static bool _loaded;
        private static GpuShaderModule _module;
        private static GpuPipelineLayout _layout;
        private static GpuRenderPipeline _pipeline;
        private static GpuBuffer _vbo;
        private static GpuBuffer _ibo;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            _module = new GpuShaderModule(ResourceReader.ReadString("System/Shaders/BlockOutline.wgsl"), "blockOutline");
            _layout = new GpuPipelineLayout(ReadOnlySpan<IntPtr>.Empty,
                ShaderStage.Vertex | ShaderStage.Fragment, (uint)sizeof(OutlinePush), "blockOutline");

            var vbDesc = new VertexBufferDesc(12, new[] { new VertexAttr(0, VertexFormat.Float32x3, 0) });
            // Three G-buffer targets, but the line fragment only writes diffuse + normal — the light attachment
            // is masked so composition leaves the unlit lines untouched.
            var targets = new[]
            {
                new ColorTargetDesc(GBufferTargets.DiffuseFormat),
                new ColorTargetDesc(GBufferTargets.NormalFormat),
                new ColorTargetDesc(GBufferTargets.LightFormat, null, ColorWriteMask.None),
            };
            // Reverse-Z depth test against the terrain (nearer = greater), no depth write.
            var depth = new DepthDesc(GBufferTargets.DepthFormat, false, CompareFunction.Greater);
            _pipeline = new GpuRenderPipeline(_layout, _module, "vs_main", "fs_main",
                new[] { vbDesc }, targets, depth,
                topology: PrimitiveTopology.LineList, cullMode: CullMode.None, label: "blockOutline");

            _vbo = new GpuBuffer((ulong)(CubeVerts.Length * sizeof(float)),
                BufferUsage.Vertex | BufferUsage.CopyDst, "outline.verts");
            _vbo.QueueWrite<float>(CubeVerts);
            _ibo = new GpuBuffer((ulong)(CubeLines.Length * sizeof(uint)),
                BufferUsage.Index | BufferUsage.CopyDst, "outline.indices");
            _ibo.QueueWrite<uint>(CubeLines);
        }

        /// <summary>Draw the unit cube as wireframe lines under the given clip-space transform and colour.</summary>
        public static void Draw(RenderPass pass, in Matrix4 mvp, in Vector4 color)
        {
            var push = new OutlinePush { Transform = MatrixConvert.ToGpu(mvp), Color = color };
            pass.SetPipeline(_pipeline);
            pass.SetVertexBuffer(0, _vbo);
            pass.SetIndexBuffer(_ibo, IndexFormat.Uint32);
            pass.SetPushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 0, in push);
            pass.DrawIndexed((uint)CubeLines.Length);
        }
    }
}

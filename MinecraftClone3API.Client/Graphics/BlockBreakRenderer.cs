using System.Collections.Generic;
using System.Runtime.InteropServices;
using MinecraftClone3API.Graphics.Rhi;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Draws the progressive block-breaking crack overlay (Minecraft's <c>destroy_stage_0..9</c>) over the block
    /// the player is mining. The textured cube is blended into the G-buffer diffuse only (the normal + light
    /// attachments are masked, so the block keeps its own shading) and composition then lights the darkened
    /// surface, so the cracks read like part of the block. Draws nothing when no resource pack supplies the
    /// stage textures.
    /// </summary>
    public static unsafe class BlockBreakRenderer
    {
        private const int Stages = 10;

        [StructLayout(LayoutKind.Sequential)]
        private struct BreakPush
        {
            public Mat4 Transform;
        }

        private static GpuShaderModule _module;
        private static GpuPipelineLayout _layout;
        private static GpuRenderPipeline _pipeline;
        private static GpuBuffer _vbo;
        private static GpuBuffer _ibo;
        private static uint _indexCount;

        private static Texture[] _stages;
        private static bool _stagesLoaded;

        public static void Load()
        {
            _module = new GpuShaderModule(ResourceReader.ReadString("System/Shaders/BlockBreak.wgsl"), "blockBreak");
            _layout = new GpuPipelineLayout(new[] { GpuPipelineLayout.Ptr(GpuLayouts.ScreenTexture) },
                ShaderStage.Vertex, (uint)sizeof(BreakPush), "blockBreak");

            var vbDesc = new VertexBufferDesc(20, new[]
            {
                new VertexAttr(0, VertexFormat.Float32x3, 0),
                new VertexAttr(1, VertexFormat.Float32x2, 12),
            });
            // Straight-alpha blend into the diffuse attachment only; the normal + light attachments are masked
            // so the block keeps its geometry-pass shading and composition lights the cracked surface as one.
            var targets = new[]
            {
                new ColorTargetDesc(GBufferTargets.DiffuseFormat, GpuRenderPipeline.AlphaBlend),
                new ColorTargetDesc(GBufferTargets.NormalFormat, null, ColorWriteMask.None),
                new ColorTargetDesc(GBufferTargets.LightFormat, null, ColorWriteMask.None),
            };
            // The crack sits a touch proud of the block (scaled 1.002x), so reverse-Z GreaterEqual keeps the
            // near faces while the far ones stay depth-culled; no depth write.
            var depth = new DepthDesc(GBufferTargets.DepthFormat, false, CompareFunction.GreaterEqual);
            _pipeline = new GpuRenderPipeline(_layout, _module, "vs_main", "fs_main",
                new[] { vbDesc }, targets, depth,
                topology: PrimitiveTopology.TriangleList, cullMode: CullMode.None, label: "blockBreak");

            BuildCube();
        }

        public static void Render(RenderPass pass, AxisAlignedBoundingBox boundingBox, Vector3 translation, float progress)
        {
            if (_pipeline == null || progress <= 0f) return;

            EnsureStages();
            var stage = (int)(progress * Stages);
            if (stage < 0) stage = 0;
            else if (stage >= Stages) stage = Stages - 1;
            var texture = _stages[stage];
            if (texture == null) return;

            var transform = Matrix4X4.CreateScale(boundingBox.Scale * 1.002f) *
                            Matrix4X4.CreateTranslation(boundingBox.Translation + translation) *
                            Renderer.View * Renderer.Projection;
            var push = new BreakPush { Transform = MatrixConvert.ToGpu(transform) };

            pass.SetPipeline(_pipeline);
            pass.SetBindGroup(0, texture.GuiBindGroup);
            pass.SetVertexBuffer(0, _vbo);
            pass.SetIndexBuffer(_ibo, IndexFormat.Uint32);
            pass.SetPushConstants(ShaderStage.Vertex, 0, in push);
            pass.DrawIndexed(_indexCount);
        }

        private static void EnsureStages()
        {
            if (_stagesLoaded) return;
            _stagesLoaded = true;

            _stages = new Texture[Stages];
            for (var i = 0; i < Stages; i++)
            {
                var path = "minecraft/textures/block/destroy_stage_" + i + ".png";
                if (ResourceReader.Exists(path)) _stages[i] = GlResources.ReadTexture(path);
            }
        }

        private static void BuildCube()
        {
            var verts = new List<float>();
            var indices = new List<uint>();

            AddFace(verts, indices, new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, -0.5f));       // +X
            AddFace(verts, indices, new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, 0.5f));     // -X
            AddFace(verts, indices, new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f));       // +Y
            AddFace(verts, indices, new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f));   // -Y
            AddFace(verts, indices, new Vector3(0.5f, -0.5f, 0.5f), new Vector3(-0.5f, -0.5f, 0.5f),
                new Vector3(-0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f));       // +Z
            AddFace(verts, indices, new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f));     // -Z

            _indexCount = (uint)indices.Count;
            _vbo = new GpuBuffer((ulong)(verts.Count * sizeof(float)),
                BufferUsage.Vertex | BufferUsage.CopyDst, "blockBreak.verts");
            _vbo.QueueWrite<float>(CollectionsMarshal.AsSpan(verts));
            _ibo = new GpuBuffer((ulong)(indices.Count * sizeof(uint)),
                BufferUsage.Index | BufferUsage.CopyDst, "blockBreak.indices");
            _ibo.QueueWrite<uint>(CollectionsMarshal.AsSpan(indices));
        }

        private static void AddFace(List<float> verts, List<uint> indices, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var baseIndex = (uint)(verts.Count / 5);
            Vertex(verts, a, 0, 0);
            Vertex(verts, b, 1, 0);
            Vertex(verts, c, 1, 1);
            Vertex(verts, d, 0, 1);

            indices.Add(baseIndex + 0);
            indices.Add(baseIndex + 1);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex + 0);
            indices.Add(baseIndex + 2);
            indices.Add(baseIndex + 3);
        }

        private static void Vertex(List<float> verts, Vector3 p, float u, float v)
        {
            verts.Add(p.X);
            verts.Add(p.Y);
            verts.Add(p.Z);
            verts.Add(u);
            verts.Add(v);
        }
    }
}

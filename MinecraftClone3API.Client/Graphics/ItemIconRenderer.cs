using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using MinecraftClone3API.Graphics.Rhi;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Renders a block into a small off-screen texture as a Minecraft-style isometric icon, cached per block
    /// id. The block is meshed in the void <see cref="IconWorld"/> (all six faces, full light) and forward-
    /// shaded by <c>ItemIcon.wgsl</c> into a per-icon rgba8unorm colour target (with its own reverse-Z depth);
    /// the GUI then samples that target. Lazy and main-thread only (it records a render pass into a transient
    /// encoder); the inventory GUI calls <see cref="GetIcon"/> while drawing.
    /// </summary>
    public static unsafe class ItemIconRenderer
    {
        public const int Size = 64;
        private const TextureFormat ColorFormat = TextureFormat.Rgba8Unorm;
        private const TextureFormat DepthFormat = TextureFormat.Depth32float;

        // Isometric camera: look down at the cube (centred on the origin, spanning [-0.5,0.5]) from the
        // top-front-right so the +Y/top, +Z/front and +X/right faces are all visible, matching MC's icons.
        private static readonly Matrix4 View =
            Matrix4X4.CreateLookAt(new Vector3(1.2f, 1.05f, 1.2f), Vector3.Zero, Vector3.UnitY);
        // Reverse-Z orthographic: near/far swapped so a greater depth wins, matching the pipeline's
        // CompareFunction.Greater and the depth-cleared-to-0 attachment.
        private static readonly Matrix4 Projection =
            Matrix4X4.CreateOrthographicOffCenter(-0.9f, 0.9f, -0.9f, 0.9f, 10f, -10f);

        // The creative inventory paperdoll: a full-body front view from slightly above, framed feet-to-head with
        // headroom above the crown so a worn helmet (the body model's outermost layer, inflated ~1px past the
        // head) isn't clipped by the frustum top. The target is taller than wide to match the player's silhouette;
        // the ortho box keeps the same aspect so the model isn't stretched. Rendered at ~2.5× the on-screen box so
        // the nearest-sampled GUI blit stays crisp instead of upscaling a small target into blocky pixels.
        public const int PlayerWidth = 200;
        public const int PlayerHeight = 280;
        private static readonly Matrix4 PlayerView =
            Matrix4X4.CreateLookAt(new Vector3(0f, 1.2f, 3.0f), new Vector3(0f, 1.0f, 0f), Vector3.UnitY);
        private static readonly Matrix4 PlayerProjection =
            Matrix4X4.CreateOrthographicOffCenter(-0.82f, 0.82f, -1.15f, 1.15f, 10f, -10f);

        [StructLayout(LayoutKind.Sequential)]
        private struct IconFrame
        {
            public Mat4 View;
            public Mat4 Proj;
        }

        private static readonly Dictionary<ushort, Texture> Cache = new Dictionary<ushort, Texture>();

        private static bool _loaded;
        private static GpuShaderModule _module;
        private static GpuBindGroupLayout _frameLayout;
        private static GpuPipelineLayout _pipelineLayout;
        private static GpuRenderPipeline _pipeline;
        private static GpuBuffer _frameUbo;
        private static GpuBindGroup _frameBind;
        private static GpuBuffer _playerFrameUbo;
        private static GpuBindGroup _playerFrameBind;
        private static GpuBindGroup _atlasBind;

        // The paperdoll re-renders every frame (it follows the cursor), so it reuses one persistent colour+depth
        // target rather than allocating per frame.
        private static GpuTexture _playerColorTex;
        private static GpuTexture _playerDepthTex;
        private static Texture _playerIcon;

        /// <summary>The icon texture for a block id (rendered and cached on first request).</summary>
        public static Texture GetIcon(ushort blockId)
        {
            if (Cache.TryGetValue(blockId, out var tex)) return tex;
            tex = Render(GameRegistry.GetBlock(blockId));
            Cache[blockId] = tex;
            return tex;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            _module = new GpuShaderModule(ResourceReader.ReadString("System/Shaders/ItemIcon.wgsl"), "itemIcon");

            // group(0): the IconFrame UBO (view, proj). group(1): the four block-atlas arrays + sampler,
            // the shared BlockAtlas layout the geometry pass also uses.
            _frameLayout = new GpuBindGroupLayout(new[]
            {
                GpuBindGroupLayout.Buffer(0, ShaderStage.Vertex, BufferBindingType.Uniform),
            }, "itemIcon.frame");

            _pipelineLayout = new GpuPipelineLayout(new[]
            {
                GpuPipelineLayout.Ptr(_frameLayout),
                GpuPipelineLayout.Ptr(GpuLayouts.BlockAtlas),
            }, label: "itemIcon");

            // Straight-alpha blend so the discard-driven transparent background composites correctly into the
            // cleared (transparent) colour target. No face culling: non-full models (stairs, etc.) rely on
            // their back faces being visible. Reverse-Z depth writes so near cube faces occlude the far ones.
            var color = new ColorTargetDesc(ColorFormat, GpuRenderPipeline.AlphaBlend);
            var depth = new DepthDesc(DepthFormat, true, CompareFunction.Greater);
            _pipeline = new GpuRenderPipeline(_pipelineLayout, _module, "vs_main", "fs_main",
                ChunkMeshArena.GeometryVertexLayout, stackalloc[] { color }, depth, label: "itemIcon");

            _frameUbo = new GpuBuffer((ulong)sizeof(IconFrame),
                BufferUsage.Uniform | BufferUsage.CopyDst, "itemIcon.frame");
            _frameUbo.QueueWriteStruct(new IconFrame
            {
                View = MatrixConvert.ToGpu(View),
                Proj = MatrixConvert.ToGpu(Projection),
            });
            _frameBind = new GpuBindGroup(_frameLayout, new[] { GpuBindGroup.Buffer(0, _frameUbo) }, "itemIcon.frame");

            _playerFrameUbo = new GpuBuffer((ulong)sizeof(IconFrame),
                BufferUsage.Uniform | BufferUsage.CopyDst, "itemIcon.playerFrame");
            _playerFrameUbo.QueueWriteStruct(new IconFrame
            {
                View = MatrixConvert.ToGpu(PlayerView),
                Proj = MatrixConvert.ToGpu(PlayerProjection),
            });
            _playerFrameBind = new GpuBindGroup(_frameLayout,
                new[] { GpuBindGroup.Buffer(0, _playerFrameUbo) }, "itemIcon.playerFrame");

            _atlasBind = new GpuBindGroup(GpuLayouts.BlockAtlas, new[]
            {
                GpuBindGroup.Texture(0, BlockTextureUploader.ArrayAt(0).View),
                GpuBindGroup.Texture(1, BlockTextureUploader.ArrayAt(1).View),
                GpuBindGroup.Texture(2, BlockTextureUploader.ArrayAt(2).View),
                GpuBindGroup.Texture(3, BlockTextureUploader.ArrayAt(3).View),
                GpuBindGroup.Sampler(4, GpuSamplers.Block),
            }, "itemIcon.atlas");
        }

        /// <summary>Renders the full-body player paperdoll for the creative inventory's Survival-Inventory tab and
        /// returns its texture. <paramref name="bodyTransform"/> orients the whole model (body yaw) and
        /// <paramref name="headYaw"/>/<paramref name="headPitch"/> turn the head — so the model looks toward the
        /// cursor; <paramref name="armor"/> are the four worn pieces drawn over the body. Re-renders into one
        /// persistent target each call; main-thread only.</summary>
        public static Texture RenderPlayer(Matrix4 bodyTransform, float headYaw, float headPitch, ushort[] armor)
        {
            EnsureLoaded();
            EnsurePlayerTarget();
            var mesh = EntityRenderer.BuildPlayerIconMesh(bodyTransform, headYaw, headPitch, armor);
            RenderPlayerMesh(mesh);
            return _playerIcon;
        }

        private static void EnsurePlayerTarget()
        {
            if (_playerColorTex != null) return;
            _playerColorTex = new GpuTexture(PlayerWidth, PlayerHeight, ColorFormat,
                TextureUsage.RenderAttachment | TextureUsage.TextureBinding, label: "playerIcon.color");
            _playerDepthTex = new GpuTexture(PlayerWidth, PlayerHeight, DepthFormat,
                TextureUsage.RenderAttachment, label: "playerIcon.depth");
            _playerIcon = Texture.FromGpu(_playerColorTex);
        }

        private static void RenderPlayerMesh(MeshBuffer mesh)
        {
            var streams = UploadMesh(mesh);
            var encoder = GpuCommandEncoder.Create("playerIcon");
            var pass = RenderPassBuilder.Begin(encoder,
                stackalloc[] { ColorAttachment.ClearTo(_playerColorTex.View, 0, 0, 0, 0) },
                new DepthAttachment(_playerDepthTex.View, LoadOp.Clear, 0f));

            pass.SetPipeline(_pipeline);
            pass.SetBindGroup(0, _playerFrameBind);
            pass.SetBindGroup(1, _atlasBind);
            for (uint i = 0; i < 5; i++) pass.SetVertexBuffer(i, streams.Vbo[i]);
            pass.SetIndexBuffer(streams.Ibo, IndexFormat.Uint32);
            pass.DrawIndexed((uint)mesh.IndicesCount);

            pass.End();
            pass.Release();
            encoder.SubmitImmediate("playerIcon");

            for (var i = 0; i < 5; i++) streams.Vbo[i].Dispose();
            streams.Ibo.Dispose();
            mesh.Clear();
        }

        private static Texture Render(Block block)
        {
            EnsureLoaded();

            MeshBuffer mesh;
            if (block.RendersAsBlockEntity && block.BlockEntityModelPath != null)
                // Block entities (chests) have no chunk-mesh cube; bake their box model at rest, lowered to sit
                // centred in the icon frame (the model is authored centred on x/z with its feet at y=0).
                mesh = EntityRenderer.BuildBlockEntityIconMesh(block.BlockEntityModelPath, block.BlockEntityTexturePath,
                    Matrix4X4.CreateTranslation(0f, -0.45f, 0f));
            else
            {
                mesh = new MeshBuffer();
                // The -0.5 origin offset re-centres the corner-origin [0,1] cell mesh on the origin (where the iso
                // camera looks).
                ChunkMesher.AddBlockToVao(IconWorld.Instance, Vector3i.Zero, 0, 0, 0, block, mesh, mesh, new Vector3(-0.5f));
            }

            return RenderMeshToTexture(mesh, _frameBind, Size, Size);
        }

        /// <summary>Forward-renders a baked mesh into a fresh <paramref name="width"/>×<paramref name="height"/>
        /// rgba8 colour target (its own reverse-Z depth) under the given frame camera, and returns it as a
        /// GUI-samplable texture. Transient buffers are submitted immediately and released.</summary>
        private static Texture RenderMeshToTexture(MeshBuffer mesh, GpuBindGroup frameBind, int width, int height)
        {
            var colorTex = new GpuTexture((uint) width, (uint) height, ColorFormat,
                TextureUsage.RenderAttachment | TextureUsage.TextureBinding, label: "itemIcon.color");
            var icon = Texture.FromGpu(colorTex);

            if (mesh.IndicesCount == 0) return icon;

            var streams = UploadMesh(mesh);
            var depthTex = new GpuTexture((uint) width, (uint) height, DepthFormat, TextureUsage.RenderAttachment, label: "itemIcon.depth");

            var encoder = GpuCommandEncoder.Create("itemIcon");
            var pass = RenderPassBuilder.Begin(encoder,
                stackalloc[] { ColorAttachment.ClearTo(colorTex.View, 0, 0, 0, 0) },
                new DepthAttachment(depthTex.View, LoadOp.Clear, 0f));

            pass.SetPipeline(_pipeline);
            pass.SetBindGroup(0, frameBind);
            pass.SetBindGroup(1, _atlasBind);
            for (uint i = 0; i < 5; i++) pass.SetVertexBuffer(i, streams.Vbo[i]);
            pass.SetIndexBuffer(streams.Ibo, IndexFormat.Uint32);
            pass.DrawIndexed((uint)mesh.IndicesCount);

            pass.End();
            pass.Release();
            encoder.SubmitImmediate("itemIcon");

            for (var i = 0; i < 5; i++) streams.Vbo[i].Dispose();
            streams.Ibo.Dispose();
            depthTex.Dispose();
            mesh.Clear();

            return icon;
        }

        private struct MeshStreams { public GpuBuffer[] Vbo; public GpuBuffer Ibo; }

        private static MeshStreams UploadMesh(MeshBuffer mesh)
        {
            var vbo = new GpuBuffer[5];
            vbo[0] = StreamBuffer(mesh.Positions, 12, "itemIcon.pos");
            vbo[1] = StreamBuffer(mesh.Uvs, 8, "itemIcon.uv");
            vbo[2] = StreamBuffer(mesh.Packed, 4, "itemIcon.packed");
            vbo[3] = StreamBuffer(mesh.Colors, 4, "itemIcon.color");
            vbo[4] = StreamBuffer(mesh.Lights, 4, "itemIcon.light");

            var ibo = new GpuBuffer((ulong)((long)mesh.IndicesCount * sizeof(uint)),
                BufferUsage.Index | BufferUsage.CopyDst, "itemIcon.ibo");
            ibo.QueueWrite<uint>(CollectionsMarshal.AsSpan(mesh.Indices));
            return new MeshStreams { Vbo = vbo, Ibo = ibo };
        }

        private static GpuBuffer StreamBuffer<T>(List<T> data, int elemBytes, string label) where T : unmanaged
        {
            var buffer = new GpuBuffer((ulong)((long)data.Count * elemBytes),
                BufferUsage.Vertex | BufferUsage.CopyDst, label);
            buffer.QueueWrite<T>(CollectionsMarshal.AsSpan(data));
            return buffer;
        }
    }
}

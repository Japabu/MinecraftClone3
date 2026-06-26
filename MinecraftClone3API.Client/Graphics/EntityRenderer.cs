using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics.Rhi;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Draws every remote entity into the deferred G-buffer with <c>EntityGeometry.wgsl</c>: registered
    /// creatures/players as animated box models textured from the official Minecraft entity sheets, and dropped
    /// items as the spinning 3D icon of their block. Each model is built once (GPU buffers per part) in
    /// <see cref="LoadModels"/> — which must run after plugins register their types and before
    /// <see cref="BlockTextureUploader.Upload"/>, so the entity textures make it into the arrays. Animation
    /// (limb swing, item spin) is matrix-only at draw time; the shared model meshes are static.
    ///
    /// <para>Per-part transforms ride one dynamic-offset uniform buffer (group 1): every part of every entity
    /// this frame writes one 256-byte-aligned <see cref="EntityDraw"/> slot, then each part draw binds group 1
    /// at its slot offset. Entities write real normals + a flat per-entity light into the G-buffer (material
    /// .w = 0 => lit), so they receive sun/shadow and block light like the terrain.</para>
    /// </summary>
    public static unsafe class EntityRenderer
    {
        private const string PlayerSkinPath = "minecraft:entity/player/wide/steve";
        private const string PlayerSkinPathLegacy = "minecraft:entity/steve";
        private const string PlayerModelPath = "System/Models/Entity/player.geo.json";

        /// <summary>Per-draw uniform matching the WGSL <c>EntityDraw</c> in EntityGeometry.wgsl (group 1).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct EntityDraw
        {
            public Mat4 Model;
            public Vector4 Light;
        }

        // wgpu requires a dynamic-offset UBO's bound slots be aligned to 256 bytes.
        private const int SlotAlign = 256;

        /// <summary>A part's GPU mesh: the five packed vertex streams + index buffer. Built from a CPU
        /// <see cref="MeshBuffer"/> once at load.</summary>
        private sealed class PartMesh
        {
            private static readonly int[] AttribBytes = {12, 8, 4, 4, 4};

            private readonly GpuBuffer[] _vbo = new GpuBuffer[5];
            private readonly GpuBuffer _ibo;
            private readonly uint _indexCount;

            public PartMesh(MeshBuffer mesh)
            {
                _vbo[0] = Stream<Vector3>(mesh.Positions, 0);
                _vbo[1] = Stream<Vector2>(mesh.Uvs, 1);
                _vbo[2] = Stream<uint>(mesh.Packed, 2);
                _vbo[3] = Stream<uint>(mesh.Colors, 3);
                _vbo[4] = Stream<uint>(mesh.Lights, 4);

                _indexCount = (uint) mesh.IndicesCount;
                _ibo = new GpuBuffer((ulong) (mesh.IndicesCount * sizeof(uint)),
                    BufferUsage.Index | BufferUsage.CopyDst, "entityPart.ibo");
                _ibo.QueueWrite<uint>(CollectionsMarshal.AsSpan(mesh.Indices));
            }

            private static GpuBuffer Stream<T>(List<T> data, int slot) where T : unmanaged
            {
                var buffer = new GpuBuffer((ulong) (data.Count * AttribBytes[slot]),
                    BufferUsage.Vertex | BufferUsage.CopyDst, $"entityPart.vbo{slot}");
                buffer.QueueWrite<T>(CollectionsMarshal.AsSpan(data));
                return buffer;
            }

            public void Draw(RenderPass pass)
            {
                for (uint i = 0; i < 5; i++) pass.SetVertexBuffer(i, _vbo[i]);
                pass.SetIndexBuffer(_ibo, IndexFormat.Uint32);
                pass.DrawIndexed(_indexCount);
            }
        }

        private class RenderModel
        {
            public BlockTexture Texture;
            // Optional second layer drawn over this one with its own texture (the sheep's wool); suppressed when
            // the entity is sheared.
            public RenderModel Overlay;
            public readonly List<(ModelPart Part, PartMesh Mesh)> Parts = new List<(ModelPart, PartMesh)>();
        }

        private static readonly Dictionary<ushort, RenderModel> CreatureModels = new Dictionary<ushort, RenderModel>();
        private static RenderModel _playerModel;

        // Dropped-item meshes (the block's icon geometry), built lazily — block textures are already uploaded.
        private static readonly Dictionary<ushort, PartMesh> ItemMeshes = new Dictionary<ushort, PartMesh>();

        private static readonly Stopwatch AnimClock = Stopwatch.StartNew();

        private static GpuShaderModule _module;
        private static GpuBindGroupLayout _entityLayout;
        private static GpuPipelineLayout _pipelineLayout;
        private static GpuRenderPipeline _pipeline;

        // The dynamic-offset slot UBO + its bind group, grown as the per-frame draw count rises. The group-2
        // atlas bind group is (re)built when the texture arrays change reference (after a texture re-upload).
        private static GpuBuffer _drawUbo;
        private static GpuBindGroup _drawBind;
        private static GpuBindGroup _atlasBind;
        private static GpuTexture[] _atlasArrays;

        private static readonly List<EntityDraw> _draws = new List<EntityDraw>(256);
        private static readonly List<(PartMesh Mesh, int Slot)> _queue = new List<(PartMesh, int)>(256);

        /// <summary>Builds the entity pipeline. The per-type GPU models are built later in <see cref="LoadModels"/>,
        /// once entity types are registered.</summary>
        public static void Load()
        {
            _module = new GpuShaderModule(ResourceReader.ReadString("System/Shaders/EntityGeometry.wgsl"), "entityGeometry");

            _entityLayout = new GpuBindGroupLayout(new[]
            {
                GpuBindGroupLayout.Buffer(0, ShaderStage.Vertex | ShaderStage.Fragment, BufferBindingType.Uniform,
                    dynamicOffset: true, minBindingSize: (ulong) Marshal.SizeOf<EntityDraw>()),
            }, "entityDraw");

            _pipelineLayout = new GpuPipelineLayout(new[]
            {
                GpuPipelineLayout.Ptr(GpuLayouts.Frame),
                GpuPipelineLayout.Ptr(_entityLayout),
                GpuPipelineLayout.Ptr(GpuLayouts.BlockAtlas),
            }, label: "entity");

            var formats = GBufferTargets.ColorFormats;
            Span<ColorTargetDesc> colors = stackalloc ColorTargetDesc[formats.Length];
            for (var i = 0; i < formats.Length; i++) colors[i] = new ColorTargetDesc(formats[i]);

            // Box models emit all six faces of every box; drawing with culling would drop the back faces, so
            // cull none (depth resolves visibility). Entities write depth (reverse-Z, Greater).
            _pipeline = new GpuRenderPipeline(_pipelineLayout, _module, "vs_main", "fs_main",
                ChunkMeshArena.GeometryVertexLayout, colors,
                depth: new DepthDesc(GBufferTargets.DepthFormat, true, CompareFunction.Greater),
                cullMode: CullMode.None, label: "entity");
        }

        /// <summary>Builds the GPU model for every registered creature type plus the built-in player, registering
        /// their textures into <see cref="BlockTextureManager"/>. Must run after plugin load and before the
        /// texture-array upload. Main-thread only.</summary>
        public static void LoadModels()
        {
            var skinPath = ResolvePath(PlayerSkinPath) ?? ResolvePath(PlayerSkinPathLegacy);
            _playerModel = BuildModel(LoadModel(PlayerModelPath), LoadPlayerSkin(), ReadData(skinPath));

            foreach (var type in GameRegistry.EntityTypes)
            {
                if (type.Kind != EntityKind.Creature || type.ModelPath == null) continue;
                var texture = ResourceReader.ReadBlockTexture(type.TexturePath);
                var model = BuildModel(LoadModel(type.ModelPath), texture, ReadData(ResolvePath(type.TexturePath)));
                if (type.OverlayModelPath != null)
                    model.Overlay = BuildModel(LoadModel(type.OverlayModelPath),
                        ResourceReader.ReadBlockTexture(type.OverlayTexturePath),
                        ReadData(ResolvePath(type.OverlayTexturePath)));
                CreatureModels[type.Id] = model;
            }
        }

        private static BlockTexture LoadPlayerSkin()
        {
            var skin = ResourceReader.ReadBlockTexture(PlayerSkinPath);
            if (skin == CommonResources.MissingTexture) skin = ResourceReader.ReadBlockTexture(PlayerSkinPathLegacy);
            return skin;
        }

        private static EntityModel LoadModel(string path) => BedrockModelLoader.Parse(ResourceReader.ReadString(path));

        private static string ResolvePath(string path) =>
            path == null ? null : BlockModel.GetRelativePaths(path, path, ".png").FirstOrDefault(ResourceReader.Exists);

        private static TextureData ReadData(string resolved) =>
            resolved == null ? null : ResourceReader.ReadTextureData(resolved);

        private static RenderModel BuildModel(EntityModel model, BlockTexture texture, TextureData data)
        {
            var render = new RenderModel {Texture = texture};
            var layerSize = BlockTextureManager.Sizes[texture.ArrayId];

            MirrorEmptyLimbs(model, data);

            foreach (var part in model.Parts)
            {
                var mesh = new MeshBuffer();
                foreach (var box in part.Boxes)
                    AddBox(mesh, box, texture, layerSize);
                render.Parts.Add((part, new PartMesh(mesh)));
                mesh.Clear();
            }

            return render;
        }

        // Some entity sheets (this pack's zombie) use the legacy single-layer humanoid layout: the left arm/leg
        // regions are empty and the right-limb texels serve both sides. When a left part maps to a fully
        // transparent region, copy the matching right part's UVs so it textures from the populated right limb
        // instead of being discarded by the shader. A modern skin (the player's) paints real left limbs, so its
        // regions aren't empty and nothing is remapped.
        private static void MirrorEmptyLimbs(EntityModel model, TextureData data)
        {
            if (data == null) return;
            MirrorPart(model, "arm1", "arm0", data);
            MirrorPart(model, "leg1", "leg0", data);
        }

        private static void MirrorPart(EntityModel model, string leftName, string rightName, TextureData data)
        {
            var left = model.Parts.Find(p => p.Name == leftName);
            var right = model.Parts.Find(p => p.Name == rightName);
            if (left == null || right == null) return;

            for (var i = 0; i < left.Boxes.Count && i < right.Boxes.Count; i++)
                if (BoxRegionEmpty(left.Boxes[i], data))
                {
                    left.Boxes[i].TexU = right.Boxes[i].TexU;
                    left.Boxes[i].TexV = right.Boxes[i].TexV;
                }
        }

        // True if the box's whole texture-unwrap rectangle is fully transparent in the sheet (an unpainted limb).
        private static bool BoxRegionEmpty(ModelBox box, TextureData data)
        {
            var sx = (int) MathF.Round((box.To.X - box.From.X) * 16f);
            var sy = (int) MathF.Round((box.To.Y - box.From.Y) * 16f);
            var sz = (int) MathF.Round((box.To.Z - box.From.Z) * 16f);
            var u1 = box.TexU + 2 * sz + 2 * sx;
            var v1 = box.TexV + sz + sy;

            for (var y = box.TexV; y < v1 && y < data.Height; y++)
            for (var x = box.TexU; x < u1 && x < data.Width; x++)
                if (data.Pixels[(y * data.Width + x) * 4 + 3] > 0) return false;
            return true;
        }

        public static void Render(RenderPass pass, WorldClient world, Camera camera)
        {
            // In a third-person view the local player draws its own body (it isn't in world.Entities).
            var drawSelf = PlayerController.RenderSelf && PlayerController.PlayerEntity != null;
            if (world.Entities.Count == 0 && !drawSelf) return;
            if (_pipeline == null) return;

            _draws.Clear();
            _queue.Clear();

            foreach (var entity in world.Entities.Values)
            {
                if (entity.Type == null)
                    QueueModel(_playerModel, entity, world);
                else if (entity.Type.Kind == EntityKind.Item)
                    QueueItem((EntityItem) entity, world);
                else if (entity.Type.Kind == EntityKind.FallingBlock)
                    QueueFallingBlock((EntityFallingBlock) entity, world);
                else if (CreatureModels.TryGetValue(entity.Type.Id, out var model))
                    QueueModel(model, entity, world);
            }

            if (drawSelf)
                QueueModel(_playerModel, PlayerController.PlayerEntity, world);

            if (_queue.Count == 0) return;

            UploadDraws();
            EnsureAtlasBind();

            pass.SetPipeline(_pipeline);
            pass.SetBindGroup(0, Renderer.FrameBindGroup);
            pass.SetBindGroup(2, _atlasBind);

            foreach (var (mesh, slot) in _queue)
            {
                pass.SetBindGroup(1, _drawBind, stackalloc uint[] {(uint) (slot * SlotAlign)});
                mesh.Draw(pass);
            }
        }

        private static void QueueModel(RenderModel model, Entity entity, WorldClient world)
        {
            if (model == null) return;

            var pos = entity.RenderPosition;
            var height = entity.Type?.Height ?? 1.8f;
            var light = SampleLight(world, pos + new Vector3(0, height * 0.5f, 0));

            // Whole-model placement: yaw to face the heading, then translate to the entity's feet.
            var root = Matrix4X4.CreateRotationY(entity.Yaw) * Matrix4X4.CreateTranslation(pos);

            QueueParts(model, entity, root, light);
            // The wool overlay shares the base part names/pivots, so the same animation matrices apply; the
            // entity's data (e.g. a sheared sheep) can hide it.
            if (model.Overlay != null && (entity.Data?.OverlayVisible ?? true))
                QueueParts(model.Overlay, entity, root, light);
        }

        private static void QueueParts(RenderModel model, Entity entity, Matrix4 root, Vector4 light)
        {
            foreach (var (part, mesh) in model.Parts)
            {
                var rotation = part.Rotation + PartRotation(part.Name, entity);
                var matrix = Matrix4X4.CreateRotationX(rotation.X) * Matrix4X4.CreateRotationY(rotation.Y) *
                             Matrix4X4.CreateRotationZ(rotation.Z) *
                             Matrix4X4.CreateTranslation(part.Pivot) * root;
                Enqueue(mesh, matrix, light);
            }
        }

        private static void QueueItem(EntityItem item, WorldClient world)
        {
            if (item.Stack.IsEmpty) return;
            var block = item.Stack.Item?.GetBlock();
            if (block == null) return;
            var mesh = GetItemMesh(block.Id);
            if (mesh == null) return;

            var pos = item.RenderPosition;
            var light = SampleLight(world, pos + new Vector3(0, 0.25f, 0));

            var t = (float) AnimClock.Elapsed.TotalSeconds;
            var bob = 0.1f + 0.05f * MathF.Sin(t * 3f);
            var matrix = Matrix4X4.CreateScale(0.4f) * Matrix4X4.CreateRotationY(t * 2f) *
                         Matrix4X4.CreateTranslation(pos + new Vector3(0, 0.25f + bob, 0));
            Enqueue(mesh, matrix, light);
        }

        private static void QueueFallingBlock(EntityFallingBlock falling, WorldClient world)
        {
            var mesh = GetItemMesh(falling.BlockId);
            if (mesh == null) return;

            // RenderPosition is the block's bottom-centre; the icon mesh is centred on the origin, so lift it by
            // half a block to fill the cell from feet upward (no spin/bob — it's a full block, not an item).
            var pos = falling.RenderPosition;
            var centre = pos + new Vector3(0, 0.5f, 0);
            var light = SampleLight(world, centre);

            Enqueue(mesh, Matrix4X4.CreateTranslation(centre), light);
        }

        private static void Enqueue(PartMesh mesh, Matrix4 model, Vector4 light)
        {
            _queue.Add((mesh, _draws.Count));
            _draws.Add(new EntityDraw {Model = MatrixConvert.ToGpu(model), Light = light});
        }

        private static void UploadDraws()
        {
            var needed = (ulong) (_draws.Count * SlotAlign);
            if (_drawUbo == null || _drawUbo.Size < needed)
            {
                _drawUbo?.Dispose();
                _drawBind?.Dispose();
                var size = (ulong) SlotAlign;
                while (size < needed) size *= 2;
                _drawUbo = new GpuBuffer(size, BufferUsage.Uniform | BufferUsage.CopyDst, "entityDraws");
                _drawBind = new GpuBindGroup(_entityLayout, new[]
                {
                    GpuBindGroup.Buffer(0, _drawUbo, 0, (ulong) Marshal.SizeOf<EntityDraw>()),
                }, "entityDraws");
            }

            for (var i = 0; i < _draws.Count; i++)
            {
                var draw = _draws[i];
                _drawUbo.QueueWriteStruct(draw, (ulong) (i * SlotAlign));
            }
        }

        private static void EnsureAtlasBind()
        {
            var changed = _atlasArrays == null;
            if (!changed)
                for (var i = 0; i < BlockTextureManager.Sizes.Length; i++)
                    if (_atlasArrays[i] != BlockTextureUploader.ArrayAt(i)) { changed = true; break; }

            if (!changed) return;

            _atlasArrays = new GpuTexture[BlockTextureManager.Sizes.Length];
            for (var i = 0; i < _atlasArrays.Length; i++) _atlasArrays[i] = BlockTextureUploader.ArrayAt(i);

            _atlasBind?.Dispose();
            _atlasBind = new GpuBindGroup(GpuLayouts.BlockAtlas, new[]
            {
                GpuBindGroup.Texture(0, _atlasArrays[0].View),
                GpuBindGroup.Texture(1, _atlasArrays[1].View),
                GpuBindGroup.Texture(2, _atlasArrays[2].View),
                GpuBindGroup.Texture(3, _atlasArrays[3].View),
                GpuBindGroup.Sampler(4, GpuSamplers.Block),
            }, "entityAtlas");
        }

        private static PartMesh GetItemMesh(ushort blockId)
        {
            if (ItemMeshes.TryGetValue(blockId, out var mesh)) return mesh;

            var block = GameRegistry.GetBlock(blockId);
            if (block == BlockRegistry.BlockAir || block.Model == null) return ItemMeshes[blockId] = null;

            // Mesh the block's model centred at the origin in the void icon world (all faces, full light), the
            // same geometry the inventory icon uses; the entity shader replaces the baked light with the UBO light.
            var buffer = new MeshBuffer();
            ChunkMesher.AddBlockToVao(IconWorld.Instance, Vector3i.Zero, 0, 0, 0, block, buffer, buffer);
            mesh = buffer.IndicesCount > 0 ? new PartMesh(buffer) : null;
            buffer.Clear();
            ItemMeshes[blockId] = mesh;
            return mesh;
        }

        /// <summary>Per-part animation rotation (radians) from the entity's walk cycle, dispatched by the part's
        /// role name: limbs (<c>leg*</c>/<c>arm*</c>) swing fore/aft, the head pitches with the look angle, and
        /// wings flap. Quadruped legs move on diagonals (0+3 together, 1+2 opposite); biped legs alternate.</summary>
        private static Vector3 PartRotation(string name, Entity entity)
        {
            if (name == "head")
                return new Vector3(entity.Pitch, 0, 0);

            if (name.StartsWith("leg") || name.StartsWith("arm"))
            {
                var idx = name.Length > 3 ? name[3] - '0' : 0;
                var sign = name[0] == 'a'
                    ? (idx == 0 ? -1f : 1f)               // arms swing opposite the legs
                    : (idx == 0 || idx == 3 ? 1f : -1f);  // diagonal leg pairs
                var swing = MathF.Cos(entity.LimbSwing) * entity.LimbSwingAmount * sign;
                return new Vector3(swing, 0, 0);
            }

            if (name.StartsWith("wing"))
            {
                var idx = name.Length > 4 ? name[4] - '0' : 0;
                var sign = idx == 0 ? 1f : -1f;
                var flap = 0.2f + 0.5f * (0.5f + 0.5f * MathF.Sin((float) AnimClock.Elapsed.TotalSeconds * 12f));
                return new Vector3(0, 0, flap * sign);
            }

            return Vector3.Zero;
        }

        private static Vector4 SampleLight(WorldClient world, Vector3 worldPos)
        {
            var bp = new Vector3i(BlockCoord(worldPos.X), BlockCoord(worldPos.Y), BlockCoord(worldPos.Z));
            var rgb = world.GetBlockLightLevel(bp).Vector3;
            var sky = world.GetSkyLight(bp);
            return new Vector4(Brightness(rgb.X), Brightness(rgb.Y), Brightness(rgb.Z), Brightness(sky));
        }

        // Matches ChunkMesher's per-level falloff (Base^(15-level)) so entities sit at the same brightness as the
        // blocks around them.
        private static float Brightness(float level) => MathF.Pow(0.8f, MathF.Max(15f - level, 0f));

        private static int BlockCoord(float v) => (int) MathF.Floor(v + 0.5f);

        // Builds the six textured quads of one box into the mesh. Box coords are in blocks (relative to the part
        // pivot); UVs come from the classic Minecraft box-unwrap, normalized by the texture array's layer size.
        private static void AddBox(MeshBuffer mesh, ModelBox box, BlockTexture tex, int layerSize)
        {
            // Box pixel dimensions (1 block = 16 texels) drive the unwrap rectangle widths — from the base
            // (un-inflated) size so an overlay box still maps its base texture region.
            var sx = (int) MathF.Round((box.To.X - box.From.X) * 16f);
            var sy = (int) MathF.Round((box.To.Y - box.From.Y) * 16f);
            var sz = (int) MathF.Round((box.To.Z - box.From.Z) * 16f);
            // Inflate grows the rendered geometry on every side (Minecraft's overlay-layer delta).
            var grow = new Vector3(box.Inflate);
            var f = box.From - grow;
            var t = box.To + grow;
            int u = box.TexU, v = box.TexV;

            // Left (-X) and Right (+X)
            Quad(mesh, tex, layerSize, new Vector3(-1, 0, 0),
                new Vector3(f.X, t.Y, t.Z), new Vector3(f.X, t.Y, f.Z),
                new Vector3(f.X, f.Y, t.Z), new Vector3(f.X, f.Y, f.Z),
                u, v + sz, u + sz, v + sz + sy);
            Quad(mesh, tex, layerSize, new Vector3(1, 0, 0),
                new Vector3(t.X, t.Y, f.Z), new Vector3(t.X, t.Y, t.Z),
                new Vector3(t.X, f.Y, f.Z), new Vector3(t.X, f.Y, t.Z),
                u + sz + sx, v + sz, u + 2 * sz + sx, v + sz + sy);
            // Front (+Z) and Back (-Z)
            Quad(mesh, tex, layerSize, new Vector3(0, 0, 1),
                new Vector3(t.X, t.Y, t.Z), new Vector3(f.X, t.Y, t.Z),
                new Vector3(t.X, f.Y, t.Z), new Vector3(f.X, f.Y, t.Z),
                u + sz, v + sz, u + sz + sx, v + sz + sy);
            Quad(mesh, tex, layerSize, new Vector3(0, 0, -1),
                new Vector3(f.X, t.Y, f.Z), new Vector3(t.X, t.Y, f.Z),
                new Vector3(f.X, f.Y, f.Z), new Vector3(t.X, f.Y, f.Z),
                u + 2 * sz + sx, v + sz, u + 2 * sz + 2 * sx, v + sz + sy);
            // Top (+Y) and Bottom (-Y). Minecraft's box-unwrap mirrors the down face vertically relative to the
            // up face, so the bottom quad's V runs the opposite way (v+sz→v) — without it a body laid horizontal
            // by a baked pitch shows its underside texture upside-down.
            Quad(mesh, tex, layerSize, new Vector3(0, 1, 0),
                new Vector3(t.X, t.Y, f.Z), new Vector3(f.X, t.Y, f.Z),
                new Vector3(t.X, t.Y, t.Z), new Vector3(f.X, t.Y, t.Z),
                u + sz, v, u + sz + sx, v + sz);
            Quad(mesh, tex, layerSize, new Vector3(0, -1, 0),
                new Vector3(t.X, f.Y, t.Z), new Vector3(f.X, f.Y, t.Z),
                new Vector3(t.X, f.Y, f.Z), new Vector3(f.X, f.Y, f.Z),
                u + sz + sx, v + sz, u + sz + 2 * sx, v);
        }

        private static void Quad(MeshBuffer mesh, BlockTexture tex, int layerSize, Vector3 normal,
            Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br, int u0, int v0, int u1, int v1)
        {
            var baseVertex = mesh.VertexCount;
            var n = new Vector4(normal.X, normal.Y, normal.Z, 0);
            var white = new Vector3(1);
            mesh.Add(tl, TexCoord(tex, layerSize, u0, v0), n, white, Vector4.Zero);
            mesh.Add(tr, TexCoord(tex, layerSize, u1, v0), n, white, Vector4.Zero);
            mesh.Add(bl, TexCoord(tex, layerSize, u0, v1), n, white, Vector4.Zero);
            mesh.Add(br, TexCoord(tex, layerSize, u1, v1), n, white, Vector4.Zero);
            mesh.AddFace(baseVertex, false, Vector3.Zero);
        }

        private static Vector4 TexCoord(BlockTexture tex, int layerSize, int u, int v)
            => new Vector4((float) u / layerSize, (float) v / layerSize, tex.TextureId, tex.ArrayId);
    }
}

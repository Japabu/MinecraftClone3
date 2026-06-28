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

        // The worn-armor layers: an outer biped (helmet/chestplate/boots, inflate 1px) textured from
        // entity/equipment/humanoid/<material>, and an inner biped (leggings, inflate 0.5px so it tucks under
        // the chestplate) from entity/equipment/humanoid_leggings/<material>. Both are 64x32 sheets.
        private const string ArmorOuterModelPath = "System/Models/Entity/armor_outer.geo.json";
        private const string ArmorInnerModelPath = "System/Models/Entity/armor_inner.geo.json";
        private const string ArmorOuterTexture = "minecraft:entity/equipment/humanoid/";
        private const string ArmorInnerTexture = "minecraft:entity/equipment/humanoid_leggings/";
        private const string LeatherOverlay = "leather_overlay";

        // Un-dyed leather armour's default tint (Minecraft 0xA06540); leather sheets ship desaturated.
        private static readonly (int R, int G, int B) LeatherColor = (160, 101, 64);

        // Which model layer + body parts each armor slot (helmet/chest/legs/boots) covers, mirroring vanilla's
        // HumanoidArmorLayer part visibility. The chestplate and leggings both plate the body box (the leggings
        // under the chestplate); boots and leggings both plate the legs.
        private static readonly (bool Inner, string[] Parts)[] ArmorParts =
        {
            (false, new[] {"head"}),
            (false, new[] {"body", "arm0", "arm1"}),
            (true, new[] {"body", "leg0", "leg1"}),
            (false, new[] {"leg0", "leg1"}),
        };

        // A material's two worn layers: the animated world models (parts looked up by name) plus the registered
        // textures the inventory icon bakes against directly.
        private sealed class ArmorSet
        {
            public RenderModel Outer;
            public RenderModel Inner;
            public BlockTexture OuterTex;
            public BlockTexture InnerTex;
        }

        private static readonly Dictionary<string, ArmorSet> ArmorSets = new Dictionary<string, ArmorSet>();
        private static EntityModel _armorOuterModel;
        private static EntityModel _armorInnerModel;

        /// <summary>Per-draw uniform matching the WGSL <c>EntityDraw</c> in EntityGeometry.wgsl (group 1).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct EntityDraw
        {
            public Mat4 Model;
            public Vector4 Light;
            public Vector4 Tint;
        }

        // wgpu requires a dynamic-offset UBO's bound slots be aligned to 256 bytes.
        private const int SlotAlign = 256;

        /// <summary>A part's GPU mesh: the five packed vertex streams + index buffer. Built from a CPU
        /// <see cref="MeshBuffer"/> once at load.</summary>
        internal sealed class PartMesh
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

        internal class RenderModel
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

        // Projectile billboard-quad meshes, keyed by entity-type id, built lazily.
        private static readonly Dictionary<ushort, PartMesh> ProjectileMeshes = new Dictionary<ushort, PartMesh>();

        private static readonly Stopwatch AnimClock = Stopwatch.StartNew();

        private static GpuShaderModule _module;
        private static GpuBindGroupLayout _entityLayout;
        private static GpuPipelineLayout _pipelineLayout;
        private static GpuRenderPipeline _pipeline;

        // The group-2 atlas bind group is (re)built when the texture arrays change reference (after a re-upload).
        private static GpuBindGroup _atlasBind;
        private static GpuTexture[] _atlasArrays;

        // Entities' own per-frame draw list. The block-entity renderer and the held-item viewmodel each own a
        // separate <see cref="EntityDrawList"/>, all sharing this class's pipeline + atlas bind.
        private static readonly EntityDrawList _list = new EntityDrawList("entity");

        /// <summary>A reusable per-frame list of box-model part draws sharing the entity pipeline + atlas. Each
        /// part writes one 256-byte-aligned <see cref="EntityDraw"/> slot into a growing UBO, then binds group 1
        /// at the slot offset to draw. <see cref="EntityRenderer"/>, the block-entity renderer, and the held-item
        /// viewmodel each own one.</summary>
        internal sealed class EntityDrawList
        {
            private readonly string _label;
            private readonly List<EntityDraw> _draws = new List<EntityDraw>(64);
            private readonly List<(PartMesh Mesh, int Slot)> _queue = new List<(PartMesh, int)>(64);
            private GpuBuffer _drawUbo;
            private GpuBindGroup _drawBind;

            public EntityDrawList(string label) => _label = label;

            public int Count => _queue.Count;

            public void Clear()
            {
                _draws.Clear();
                _queue.Clear();
            }

            public void Enqueue(PartMesh mesh, Matrix4 model, Vector4 light) => Enqueue(mesh, model, light, Vector4.One);

            public void Enqueue(PartMesh mesh, Matrix4 model, Vector4 light, Vector4 tint)
            {
                _queue.Add((mesh, _draws.Count));
                _draws.Add(new EntityDraw {Model = MatrixConvert.ToGpu(model), Light = light, Tint = tint});
            }

            public void Flush(RenderPass pass)
            {
                if (_queue.Count == 0 || _pipeline == null) return;

                UploadDraws();
                EnsureAtlasBind();

                pass.SetPipeline(_pipeline);
                pass.SetBindGroup(0, Renderer.FrameBindGroup);
                pass.SetBindGroup(2, _atlasBind);

                Span<uint> dynOffset = stackalloc uint[1];
                foreach (var (mesh, slot) in _queue)
                {
                    dynOffset[0] = (uint) (slot * SlotAlign);
                    pass.SetBindGroup(1, _drawBind, dynOffset);
                    mesh.Draw(pass);
                }
            }

            private void UploadDraws()
            {
                var needed = (ulong) (_draws.Count * SlotAlign);
                if (_drawUbo == null || _drawUbo.Size < needed)
                {
                    _drawUbo?.Dispose();
                    _drawBind?.Dispose();
                    var size = (ulong) SlotAlign;
                    while (size < needed) size *= 2;
                    _drawUbo = new GpuBuffer(size, BufferUsage.Uniform | BufferUsage.CopyDst, _label + ".draws");
                    _drawBind = new GpuBindGroup(_entityLayout, new[]
                    {
                        GpuBindGroup.Buffer(0, _drawUbo, 0, (ulong) Marshal.SizeOf<EntityDraw>()),
                    }, _label + ".draws");
                }

                for (var i = 0; i < _draws.Count; i++)
                    _drawUbo.QueueWriteStruct(_draws[i], (ulong) (i * SlotAlign));
            }
        }

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

            LoadArmor();
        }

        // Builds the two worn-armor layer models for every registered armor material, registering their
        // (square-padded) equipment textures into the atlas. Runs inside LoadModels, before the texture upload.
        private static void LoadArmor()
        {
            _armorOuterModel = LoadModel(ArmorOuterModelPath);
            _armorInnerModel = LoadModel(ArmorInnerModelPath);

            var materials = new HashSet<string>();
            foreach (var item in GameRegistry.Items)
                if (item.ArmorMaterial != null) materials.Add(item.ArmorMaterial);

            foreach (var material in materials)
            {
                if (!TryLoadArmorTexture(ArmorOuterTexture + material, material, out var outerTex) ||
                    !TryLoadArmorTexture(ArmorInnerTexture + material, material, out var innerTex))
                    continue;

                ArmorSets[material] = new ArmorSet
                {
                    OuterTex = outerTex,
                    InnerTex = innerTex,
                    Outer = BuildModel(_armorOuterModel, outerTex, null),
                    Inner = BuildModel(_armorInnerModel, innerTex, null),
                };
            }
        }

        // Loads one armor-layer sheet: read the 64x32 equipment texture (compositing the leather tint + strap
        // overlay for leather), pad it to a square atlas layer, and register it. False if the pack lacks it.
        private static bool TryLoadArmorTexture(string path, string material, out BlockTexture texture)
        {
            texture = default;
            var resolved = ResolvePath(path);
            if (resolved == null) return false;

            var data = ResourceReader.ReadTextureData(resolved);
            if (material == "leather") TintLeather(data, path);
            texture = BlockTextureManager.LoadTexture(PadToSquare(data));
            return true;
        }

        // Multiplies the desaturated leather sheet by the default leather colour, then alpha-composites the
        // (un-tinted) strap overlay from the same directory over it, baking one ready-to-sample texture.
        private static void TintLeather(TextureData data, string path)
        {
            var px = data.Pixels;
            for (var i = 0; i < px.Length; i += 4)
            {
                px[i + 0] = (byte) (px[i + 0] * LeatherColor.R / 255);
                px[i + 1] = (byte) (px[i + 1] * LeatherColor.G / 255);
                px[i + 2] = (byte) (px[i + 2] * LeatherColor.B / 255);
            }

            var overlayResolved = ResolvePath(path.Substring(0, path.LastIndexOf('/') + 1) + LeatherOverlay);
            if (overlayResolved == null) return;
            var overlay = ResourceReader.ReadTextureData(overlayResolved);
            if (overlay.Width != data.Width || overlay.Height != data.Height) return;

            var ov = overlay.Pixels;
            for (var i = 0; i < px.Length; i += 4)
            {
                int a = ov[i + 3];
                if (a == 0) continue;
                px[i + 0] = (byte) ((ov[i + 0] * a + px[i + 0] * (255 - a)) / 255);
                px[i + 1] = (byte) ((ov[i + 1] * a + px[i + 1] * (255 - a)) / 255);
                px[i + 2] = (byte) ((ov[i + 2] * a + px[i + 2] * (255 - a)) / 255);
                px[i + 3] = (byte) Math.Max(px[i + 3], a);
            }
        }

        // Copies a non-square sheet into the top-left of a transparent square (max side) layer so it uploads into
        // the square atlas array cleanly; the armor UVs only ever sample the original (top) region.
        private static TextureData PadToSquare(TextureData data)
        {
            if (data.Width == data.Height) return data;
            var side = Math.Max(data.Width, data.Height);
            var px = new byte[side * side * 4];
            for (var y = 0; y < data.Height; y++)
                Array.Copy(data.Pixels, y * data.Width * 4, px, y * side * 4, data.Width * 4);
            return new TextureData(px, side, side);
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

        /// <summary>Builds a box model from a Bedrock model + texture path, with the block-entity UV unwrap
        /// (top/bottom not flipped). Used by the block-entity renderer (chests) and the held-item viewmodel.</summary>
        internal static RenderModel BuildModelFromPaths(string modelPath, string texturePath)
        {
            var texture = ResourceReader.ReadBlockTexture(texturePath);
            return BuildModel(LoadModel(modelPath), texture, ReadData(ResolvePath(texturePath)), flipUv: false);
        }

        // flipUv mirrors the up/down face V the way the living-entity sheets are unwrapped (so a body laid flat by
        // a baked pitch shows its underside the right way up). Block entities (chests) author the opposite unwrap,
        // so they pass flipUv: false.
        private static RenderModel BuildModel(EntityModel model, BlockTexture texture, TextureData data,
            bool flipUv = true)
        {
            var render = new RenderModel {Texture = texture};
            var layerSize = BlockTextureManager.Sizes[texture.ArrayId];

            MirrorEmptyLimbs(model, data);

            foreach (var part in model.Parts)
            {
                var mesh = new MeshBuffer();
                foreach (var box in part.Boxes)
                    AddBox(mesh, box, texture, layerSize, flipUv);
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

            _list.Clear();

            foreach (var entity in world.Entities.Values)
            {
                if (entity.Type == null)
                    QueueModel(_playerModel, entity, world);
                else if (entity.Type.Kind == EntityKind.Item)
                    QueueItem((EntityItem) entity, world);
                else if (entity.Type.Kind == EntityKind.FallingBlock)
                    QueueFallingBlock((EntityFallingBlock) entity, world);
                else if (entity.Type.Kind == EntityKind.Projectile)
                    QueueProjectile(entity, world, camera);
                else if (CreatureModels.TryGetValue(entity.Type.Id, out var model))
                    QueueModel(model, entity, world);
            }

            if (drawSelf)
            {
                // The local player isn't network-synced to itself, so fill its held item + worn armor from the
                // live inventory so the third-person view shows what's selected/worn.
                var self = PlayerController.PlayerEntity;
                self.HeldItemId = (ushort) world.Inventory.SelectedItem.ItemId;
                for (var i = 0; i < self.Armor.Length; i++)
                    self.Armor[i] = (ushort) world.Inventory.Armor[i].ItemId;
                QueueModel(_playerModel, self, world);
            }

            _list.Flush(pass);
        }

        private static void QueueModel(RenderModel model, Entity entity, WorldClient world)
        {
            if (model == null) return;

            var pos = entity.RenderPosition;
            var height = entity.Type?.Height ?? 1.8f;
            var light = SampleLight(world, pos + new Vector3(0, height * 0.5f, 0));

            // Whole-model placement: yaw to face the heading, then translate to the entity's feet.
            var root = Matrix4X4.CreateRotationY(entity.Yaw) * Matrix4X4.CreateTranslation(pos);

            // Flash red briefly after taking damage so a hit reads. The held item stays untinted (white).
            var tint = HurtTint(entity);
            QueueParts(model, entity, root, light, tint);
            // The wool overlay shares the base part names/pivots, so the same animation matrices apply; the
            // entity's data (e.g. a sheared sheep) can hide it.
            if (model.Overlay != null && (entity.Data?.OverlayVisible ?? true))
                QueueParts(model.Overlay, entity, root, light, tint);

            // Players carry the main-hand item off the right arm so it swings with the body and other clients
            // (and the third-person self) see what's held, and wear their armor over the body.
            if (model == _playerModel)
            {
                QueueArmor(entity, root, light, tint);
                QueueHeldItem(entity, root, light);
            }
        }

        // Plates the player's worn armor over the body: each non-empty slot enqueues its layer's covered parts
        // with the same animation matrix as the matching body part, so the armor swings/turns with the body.
        private static void QueueArmor(Entity entity, Matrix4 root, Vector4 light, Vector4 tint)
        {
            for (var slot = 0; slot < entity.Armor.Length; slot++)
            {
                var id = entity.Armor[slot];
                if (id == 0) continue;
                var material = GameRegistry.GetItem(id)?.ArmorMaterial;
                if (material == null || !ArmorSets.TryGetValue(material, out var set)) continue;

                var (inner, names) = ArmorParts[slot];
                var model = inner ? set.Inner : set.Outer;
                foreach (var name in names)
                    foreach (var (part, mesh) in model.Parts)
                        if (part.Name == name)
                            _list.Enqueue(mesh, PartMatrix(part, entity, root), light, tint);
            }
        }

        private static Vector4 HurtTint(Entity entity)
            => entity.HurtTime > 0 ? new Vector4(1f, 0.35f, 0.35f, 1f) : Vector4.One;

        private static void QueueParts(RenderModel model, Entity entity, Matrix4 root, Vector4 light, Vector4 tint)
        {
            foreach (var (part, mesh) in model.Parts)
                _list.Enqueue(mesh, PartMatrix(part, entity, root), light, tint);
        }

        private static Matrix4 PartMatrix(ModelPart part, Entity entity, Matrix4 root)
        {
            var rotation = part.Rotation + PartRotation(part.Name, entity);
            return Matrix4X4.CreateRotationX(rotation.X) * Matrix4X4.CreateRotationY(rotation.Y) *
                   Matrix4X4.CreateRotationZ(rotation.Z) *
                   Matrix4X4.CreateTranslation(part.Pivot) * root;
        }

        // Local transforms (in the right-arm bone's space, origin at the shoulder, arm hanging to ≈y=-0.62) that
        // seat a held item in the fist. Hand-tuned against a visual check.
        private static readonly Matrix4 HeldBlockArm =
            Matrix4X4.CreateScale(0.45f) * Matrix4X4.CreateTranslation(0f, -0.55f, 0.05f);
        private static readonly Matrix4 HeldFlatArm =
            Matrix4X4.CreateScale(0.70f) *
            Matrix4X4.CreateRotationX(Scalar.DegreesToRadians(-90f)) *
            Matrix4X4.CreateRotationZ(Scalar.DegreesToRadians(180f)) *
            Matrix4X4.CreateTranslation(0f, -0.60f, 0.10f);

        // Hangs the entity's main-hand item off the right-arm bone (arm0) so it follows the arm's walk swing.
        // Blocks use their 3D icon mesh, flat items their extruded sprite, block entities their box model.
        private static void QueueHeldItem(Entity entity, Matrix4 root, Vector4 light)
        {
            if (entity.HeldItemId == 0) return;
            var item = GameRegistry.GetItem(entity.HeldItemId);
            if (item == null) return;

            ModelPart arm = null;
            foreach (var (part, _) in _playerModel.Parts)
                if (part.Name == "arm0") { arm = part; break; }
            if (arm == null) return;
            var armMatrix = PartMatrix(arm, entity, root);

            var block = item.GetBlock();
            if (block != null && block.RendersAsBlockEntity &&
                BlockEntityRenderer.GetModel(block.Id) is RenderModel beModel)
            {
                EnqueueStaticParts(_list, beModel, HeldBlockArm * armMatrix, light);
                return;
            }

            var mesh = block != null ? GetItemMesh(block.Id) : HeldItemMeshes.Get(item);
            if (mesh == null) return;
            _list.Enqueue(mesh, (block != null ? HeldBlockArm : HeldFlatArm) * armMatrix, light);
        }

        /// <summary>Enqueues a box model's parts at rest (no walk animation) onto <paramref name="list"/>, placed
        /// by <paramref name="root"/>. Used for static block entities and the held-item box-model viewmodel.</summary>
        internal static void EnqueueStaticParts(EntityDrawList list, RenderModel model, Matrix4 root, Vector4 light)
        {
            foreach (var (part, mesh) in model.Parts)
            {
                var matrix = Matrix4X4.CreateRotationX(part.Rotation.X) * Matrix4X4.CreateRotationY(part.Rotation.Y) *
                             Matrix4X4.CreateRotationZ(part.Rotation.Z) *
                             Matrix4X4.CreateTranslation(part.Pivot) * root;
                list.Enqueue(mesh, matrix, light);
            }
        }

        // A dropped item hovers, bobs and spins. A normal block uses its 3D icon mesh, a block entity (chest) its
        // box model, and a flat item its extruded sprite.
        private static void QueueItem(EntityItem item, WorldClient world)
        {
            if (item.Stack.IsEmpty) return;
            var stackItem = item.Stack.Item;
            if (stackItem == null) return;
            var block = stackItem.GetBlock();

            var pos = item.RenderPosition;
            var light = SampleLight(world, pos + new Vector3(0, 0.25f, 0));
            var t = (float) AnimClock.Elapsed.TotalSeconds;
            var bob = 0.1f + 0.05f * MathF.Sin(t * 3f);
            var centre = pos + new Vector3(0, 0.25f + bob, 0);
            var spin = Matrix4X4.CreateRotationY(t * 2f) * Matrix4X4.CreateTranslation(centre);

            if (block != null && block.RendersAsBlockEntity &&
                BlockEntityRenderer.GetModel(block.Id) is RenderModel beModel)
            {
                // Centre the box (feet at y=0, height 14/16) on the origin before scaling/spinning it.
                EnqueueStaticParts(_list, beModel,
                    Matrix4X4.CreateTranslation(0f, -0.4375f, 0f) * Matrix4X4.CreateScale(0.4f) * spin, light);
                return;
            }

            PartMesh mesh;
            float scale;
            if (block != null && block.ItemSpriteTexture != null) { mesh = HeldItemMeshes.GetByTexture(block.ItemSpriteTexture); scale = 0.5f; }
            else if (block != null) { mesh = GetItemMesh(block.Id); scale = 0.4f; }
            else { mesh = HeldItemMeshes.Get(stackItem); scale = 0.5f; }
            if (mesh == null) return;
            _list.Enqueue(mesh, Matrix4X4.CreateScale(scale) * spin, light);
        }

        // A thrown projectile (the ender pearl) renders as a camera-facing billboard quad of its item sprite.
        private static void QueueProjectile(Entity entity, WorldClient world, Camera camera)
        {
            var mesh = GetProjectileMesh(entity.Type);
            if (mesh == null) return;

            var pos = entity.RenderPosition;
            var light = SampleLight(world, pos);

            // Map the quad's local axes onto the camera basis (right, up, toward-camera) so it faces the viewer.
            var right = camera.Right;
            var up = Vector3D.Cross(camera.Right, camera.Forward);
            var toCam = -camera.Forward;
            var billboard = new Matrix4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                toCam.X, toCam.Y, toCam.Z, 0f,
                0f, 0f, 0f, 1f);
            _list.Enqueue(mesh, billboard * Matrix4X4.CreateTranslation(pos), light);
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

            _list.Enqueue(mesh, Matrix4X4.CreateTranslation(centre), light);
        }

        // A projectile's billboard quad (one quad in the local XY plane, the sprite filling its whole array
        // layer), built lazily and cached. Uses only the registered BlockTexture (which resolves the texture
        // namespace), so it works for a "minecraft:item/…" location without reading the source pixels.
        private static PartMesh GetProjectileMesh(EntityType type)
        {
            if (ProjectileMeshes.TryGetValue(type.Id, out var mesh)) return mesh;

            var tex = ResourceReader.ReadBlockTexture(type.TexturePath);
            var size = BlockTextureManager.Sizes[tex.ArrayId];
            const float r = 0.125f;   // half-extent → a 0.25-block sprite
            var buffer = new MeshBuffer();
            Quad(buffer, tex, size, new Vector3(0, 0, 1),
                new Vector3(-r, r, 0), new Vector3(r, r, 0), new Vector3(-r, -r, 0), new Vector3(r, -r, 0),
                0, 0, size, size);
            mesh = buffer.IndicesCount > 0 ? new PartMesh(buffer) : null;
            buffer.Clear();
            ProjectileMeshes[type.Id] = mesh;
            return mesh;
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
                GpuBindGroup.Sampler(4, GpuSamplers.Entity),
            }, "entityAtlas");
        }

        internal static PartMesh GetItemMesh(ushort blockId)
        {
            if (ItemMeshes.TryGetValue(blockId, out var mesh)) return mesh;

            var block = GameRegistry.GetBlock(blockId);
            if (block == BlockRegistry.BlockAir || block.Model == null) return ItemMeshes[blockId] = null;

            // Mesh the block's model centred at the origin in the void icon world (all faces, full light), the
            // same geometry the inventory icon uses; the entity shader replaces the baked light with the UBO light.
            // The -0.5 origin offset re-centres the corner-origin [0,1] cell mesh so the dropped/icon pose spins
            // about its middle.
            var buffer = new MeshBuffer();
            ChunkMesher.AddBlockToVao(IconWorld.Instance, Vector3i.Zero, 0, 0, 0, block, buffer, buffer, new Vector3(-0.5f), inventory: true);
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
                // The model loader mirrors geometry through z (z -> -z) to face it +Z, which flips the handedness,
                // so the wing on the -x side (wing0) must rotate -flap to swing OUT from the body, not into it.
                var idx = name.Length > 4 ? name[4] - '0' : 0;
                var sign = idx == 0 ? -1f : 1f;
                var flap = 0.2f + 0.5f * (0.5f + 0.5f * MathF.Sin((float) AnimClock.Elapsed.TotalSeconds * 12f));
                return new Vector3(0, 0, flap * sign);
            }

            return Vector3.Zero;
        }

        internal static Vector4 SampleLight(WorldClient world, Vector3 worldPos)
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
        private static void AddBox(MeshBuffer mesh, ModelBox box, BlockTexture tex, int layerSize, bool flipUv,
            Matrix4? transform = null, bool transformNormal = true)
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
                u, v + sz, u + sz, v + sz + sy, transform, transformNormal);
            Quad(mesh, tex, layerSize, new Vector3(1, 0, 0),
                new Vector3(t.X, t.Y, f.Z), new Vector3(t.X, t.Y, t.Z),
                new Vector3(t.X, f.Y, f.Z), new Vector3(t.X, f.Y, t.Z),
                u + sz + sx, v + sz, u + 2 * sz + sx, v + sz + sy, transform, transformNormal);
            // Front (+Z) and Back (-Z)
            Quad(mesh, tex, layerSize, new Vector3(0, 0, 1),
                new Vector3(t.X, t.Y, t.Z), new Vector3(f.X, t.Y, t.Z),
                new Vector3(t.X, f.Y, t.Z), new Vector3(f.X, f.Y, t.Z),
                u + sz, v + sz, u + sz + sx, v + sz + sy, transform, transformNormal);
            Quad(mesh, tex, layerSize, new Vector3(0, 0, -1),
                new Vector3(f.X, t.Y, f.Z), new Vector3(t.X, t.Y, f.Z),
                new Vector3(f.X, f.Y, f.Z), new Vector3(t.X, f.Y, f.Z),
                u + 2 * sz + sx, v + sz, u + 2 * sz + 2 * sx, v + sz + sy, transform, transformNormal);
            // Top (+Y) and Bottom (-Y). Minecraft's box-unwrap mirrors the down face vertically relative to the
            // up face. The living-entity sheets (flipUv) expect the up/down V swapped so a body laid horizontal by
            // a baked pitch shows its underside the right way up; block-entity sheets (chests) author the opposite
            // unwrap, so they keep the un-swapped layout.
            int tU0 = u + sz, tV0 = v, tU1 = u + sz + sx, tV1 = v + sz;
            int bU0 = u + sz + sx, bV0 = v + sz, bU1 = u + sz + 2 * sx, bV1 = v;
            if (!flipUv)
                (tU0, tV0, tU1, tV1, bU0, bV0, bU1, bV1) = (bU0, bV0, bU1, bV1, tU0, tV0, tU1, tV1);
            Quad(mesh, tex, layerSize, new Vector3(0, 1, 0),
                new Vector3(t.X, t.Y, f.Z), new Vector3(f.X, t.Y, f.Z),
                new Vector3(t.X, t.Y, t.Z), new Vector3(f.X, t.Y, t.Z),
                tU0, tV0, tU1, tV1, transform, transformNormal);
            Quad(mesh, tex, layerSize, new Vector3(0, -1, 0),
                new Vector3(t.X, f.Y, t.Z), new Vector3(f.X, f.Y, t.Z),
                new Vector3(t.X, f.Y, f.Z), new Vector3(f.X, f.Y, f.Z),
                bU0, bV0, bU1, bV1, transform, transformNormal);
        }

        internal static void Quad(MeshBuffer mesh, BlockTexture tex, int layerSize, Vector3 normal,
            Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br, int u0, int v0, int u1, int v1,
            Matrix4? transform = null, bool transformNormal = true)
        {
            if (transform.HasValue)
            {
                var m = transform.Value;
                tl = Vector3D.Transform(tl, m);
                tr = Vector3D.Transform(tr, m);
                bl = Vector3D.Transform(bl, m);
                br = Vector3D.Transform(br, m);
                // The icon shader picks a fixed brightness from the normal's quantized axis; baking a rotation into
                // the normal would re-quantize it mid-turn and make the per-face shade jump. Callers posing a model
                // for the inventory icon keep the local (model-space) normal so the shade stays stable as it turns.
                if (transformNormal) normal = Vector3D.Normalize(Vector3D.TransformNormal(normal, m));
            }

            var baseVertex = mesh.VertexCount;
            var n = new Vector4(normal.X, normal.Y, normal.Z, 0);
            var white = new Vector3(1);
            mesh.Add(tl, TexCoord(tex, layerSize, u0, v0), n, white, Vector4.Zero);
            mesh.Add(tr, TexCoord(tex, layerSize, u1, v0), n, white, Vector4.Zero);
            mesh.Add(bl, TexCoord(tex, layerSize, u0, v1), n, white, Vector4.Zero);
            mesh.Add(br, TexCoord(tex, layerSize, u1, v1), n, white, Vector4.Zero);
            mesh.AddFace(baseVertex, false, Vector3.Zero);
        }

        /// <summary>Builds a single origin-centred mesh of a block-entity model posed at rest, for the inventory
        /// icon (which draws one mesh with a baked iso camera, no per-part transform). <paramref name="centre"/>
        /// lowers the box so it sits centred in the icon frame.</summary>
        internal static MeshBuffer BuildBlockEntityIconMesh(string modelPath, string texturePath, Matrix4 centre)
        {
            var mesh = new MeshBuffer();
            BakeIconParts(mesh, LoadModel(modelPath), ResourceReader.ReadBlockTexture(texturePath), centre);
            return mesh;
        }

        /// <summary>Builds the player model for the creative inventory paperdoll (base skin + the modern overlay
        /// layer baked into <c>player.geo.json</c>, plus any worn <paramref name="armor"/>). <paramref name="centre"/>
        /// orients the whole body (a yaw toward the cursor); the head bones additionally yaw/pitch about the neck so
        /// the model looks at the cursor, as in vanilla. Feet sit at y=0, centred on x/z and facing +Z.</summary>
        public static MeshBuffer BuildPlayerIconMesh(Matrix4 centre, float headYaw, float headPitch, ushort[] armor)
        {
            var model = LoadModel(PlayerModelPath);
            var texture = LoadPlayerSkin();
            var layerSize = BlockTextureManager.Sizes[texture.ArrayId];
            var headLook = new Vector3(headPitch, headYaw, 0f);

            var mesh = new MeshBuffer();
            foreach (var part in model.Parts)
            {
                // The head bone and the hat overlay share the name "head", so both follow the look direction.
                var rot = part.Name == "head" ? part.Rotation + headLook : part.Rotation;
                var m = Matrix4X4.CreateRotationX(rot.X) * Matrix4X4.CreateRotationY(rot.Y) *
                        Matrix4X4.CreateRotationZ(rot.Z) * Matrix4X4.CreateTranslation(part.Pivot) * centre;
                // transformNormal: false — keep the model-space normal so the fixed per-face shade doesn't jump
                // (re-quantize) as the paperdoll yaws toward the cursor.
                foreach (var box in part.Boxes)
                    AddBox(mesh, box, texture, layerSize, false, m, transformNormal: false);
            }

            if (armor != null) BakeArmorParts(mesh, armor, centre, headLook);
            return mesh;
        }

        // Bakes the worn-armor layers into the paperdoll mesh (one mesh, multi-texture: each vertex carries its
        // own atlas layer). Same per-slot layer/part selection as the animated world path; the helmet follows the
        // head look so it tracks the cursor with the head.
        private static void BakeArmorParts(MeshBuffer mesh, ushort[] armor, Matrix4 centre, Vector3 headLook)
        {
            for (var slot = 0; slot < armor.Length; slot++)
            {
                var id = armor[slot];
                if (id == 0) continue;
                var material = GameRegistry.GetItem(id)?.ArmorMaterial;
                if (material == null || !ArmorSets.TryGetValue(material, out var set)) continue;

                var (inner, names) = ArmorParts[slot];
                var model = inner ? _armorInnerModel : _armorOuterModel;
                var texture = inner ? set.InnerTex : set.OuterTex;
                var layerSize = BlockTextureManager.Sizes[texture.ArrayId];

                foreach (var name in names)
                {
                    var part = model.Parts.Find(p => p.Name == name);
                    if (part == null) continue;
                    var rot = name == "head" ? part.Rotation + headLook : part.Rotation;
                    var m = Matrix4X4.CreateRotationX(rot.X) * Matrix4X4.CreateRotationY(rot.Y) *
                            Matrix4X4.CreateRotationZ(rot.Z) * Matrix4X4.CreateTranslation(part.Pivot) * centre;
                    // flipUv: true — the worn-armor sheets use the living-entity up/down unwrap (as in the world
                    // model), so the helmet crown samples the painted head-top region, not the transparent underside.
                    // transformNormal: false — stable per-face shade as the paperdoll turns (see BuildPlayerIconMesh).
                    foreach (var box in part.Boxes)
                        AddBox(mesh, box, texture, layerSize, true, m, transformNormal: false);
                }
            }
        }

        /// <summary>Bakes every part of a box model at rest (pivot + baked rotation, no animation) into one mesh
        /// under <paramref name="centre"/> — the shared body of the block-entity and player inventory icons.</summary>
        private static void BakeIconParts(MeshBuffer mesh, EntityModel model, BlockTexture texture, Matrix4 centre)
        {
            var layerSize = BlockTextureManager.Sizes[texture.ArrayId];
            foreach (var part in model.Parts)
            {
                var m = Matrix4X4.CreateRotationX(part.Rotation.X) * Matrix4X4.CreateRotationY(part.Rotation.Y) *
                        Matrix4X4.CreateRotationZ(part.Rotation.Z) *
                        Matrix4X4.CreateTranslation(part.Pivot) * centre;
                foreach (var box in part.Boxes)
                    AddBox(mesh, box, texture, layerSize, false, m);
            }
        }

        private static Vector4 TexCoord(BlockTexture tex, int layerSize, int u, int v)
            => new Vector4((float) u / layerSize, (float) v / layerSize, tex.TextureId, tex.ArrayId);
    }
}

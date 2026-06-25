using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Draws every remote entity into the deferred G-buffer with the <see cref="ClientResources.
    /// EntityGeometryShader"/>: registered creatures/players as animated box models textured from the official
    /// Minecraft entity sheets, and dropped items as the spinning 3D icon of their block. Each model is built
    /// once (GPU buffers per part) in <see cref="LoadModels"/> — which must run after plugins register their
    /// types and before <see cref="BlockTextureManager.Upload"/>, so the entity textures make it into the
    /// arrays. Animation (limb swing, item spin) is matrix-only at draw time; the shared model meshes are static.
    /// </summary>
    public static class EntityRenderer
    {
        private const string PlayerSkinPath = "minecraft:entity/player/wide/steve";
        private const string PlayerSkinPathLegacy = "minecraft:entity/steve";

        private class RenderModel
        {
            public BlockTexture Texture;
            public readonly List<(ModelPart Part, VertexArrayObject Vao)> Parts =
                new List<(ModelPart, VertexArrayObject)>();
        }

        private static readonly Dictionary<ushort, RenderModel> CreatureModels = new Dictionary<ushort, RenderModel>();
        private static RenderModel _playerModel;

        // Dropped-item meshes (the block's icon geometry), built lazily — block textures are already uploaded.
        private static readonly Dictionary<ushort, VertexArrayObject> ItemMeshes = new Dictionary<ushort, VertexArrayObject>();

        private static readonly Stopwatch AnimClock = Stopwatch.StartNew();

        /// <summary>Legacy entry point kept for the loading flow; the real work is in <see cref="LoadModels"/>,
        /// called later once entity types are registered.</summary>
        public static void Load()
        {
        }

        /// <summary>Builds the GPU model for every registered creature type plus the built-in player, registering
        /// their textures into <see cref="BlockTextureManager"/>. Must run after plugin load and before the
        /// texture-array upload. Main-thread (GL) only.</summary>
        public static void LoadModels()
        {
            var skinPath = ResolvePath(PlayerSkinPath) ?? ResolvePath(PlayerSkinPathLegacy);
            _playerModel = BuildModel(EntityModels.Biped(), LoadPlayerSkin(), ReadData(skinPath));

            foreach (var type in GameRegistry.EntityTypes)
            {
                if (type.Kind != EntityKind.Creature || type.ModelFactory == null) continue;
                var texture = ResourceReader.ReadBlockTexture(type.TexturePath);
                CreatureModels[type.Id] = BuildModel(type.ModelFactory(), texture, ReadData(ResolvePath(type.TexturePath)));
            }
        }

        private static BlockTexture LoadPlayerSkin()
        {
            var skin = ResourceReader.ReadBlockTexture(PlayerSkinPath);
            if (skin == ClientResources.MissingTexture) skin = ResourceReader.ReadBlockTexture(PlayerSkinPathLegacy);
            return skin;
        }

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
                var vao = new VertexArrayObject();
                foreach (var box in part.Boxes)
                    AddBox(vao, box, texture, layerSize);
                vao.Upload();
                render.Parts.Add((part, vao));
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

        public static void Render(WorldClient world, Camera camera, Matrix4 projection)
        {
            if (world.Entities.Count == 0) return;

            var shader = ClientResources.EntityGeometryShader;
            shader.Bind();
            var uModel = shader.GetUniformLocation("uModel");
            var uLight = shader.GetUniformLocation("uLight");
            GL.UniformMatrix4(shader.GetUniformLocation("uView"), false, ref camera.View);
            GL.UniformMatrix4(shader.GetUniformLocation("uProjection"), false, ref projection);
            GL.Uniform1(shader.GetUniformLocation("uTextures16"), 0);
            GL.Uniform1(shader.GetUniformLocation("uTextures64"), 1);
            GL.Uniform1(shader.GetUniformLocation("uTextures256"), 2);
            GL.Uniform1(shader.GetUniformLocation("uTextures1024"), 3);

            BlockTextureManager.Bind();
            Samplers.BindBlockTextureSampler();

            // Box models emit all six faces of every box; drawing them with culling on would drop the
            // back-facing ones, so disable culling here (depth still resolves what's visible) and restore after.
            RenderState.Set(new GlState {CullFace = false, DepthTest = true, DepthFunc = DepthFunction.Lequal});

            foreach (var entity in world.Entities.Values)
            {
                if (entity.Type == null)
                    DrawModel(_playerModel, entity, world, uModel, uLight);
                else if (entity.Type.Kind == EntityKind.Item)
                    DrawItem((EntityItem) entity, world, uModel, uLight);
                else if (CreatureModels.TryGetValue(entity.Type.Id, out var model))
                    DrawModel(model, entity, world, uModel, uLight);
            }

            RenderState.Set(new GlState {CullFace = true, DepthTest = true, DepthFunc = DepthFunction.Lequal});
        }

        private static void DrawModel(RenderModel model, Entity entity, WorldClient world, int uModel, int uLight)
        {
            if (model == null) return;

            var pos = entity.RenderPosition;
            var height = entity.Type?.Height ?? 1.8f;
            SetLight(world, pos + new Vector3(0, height * 0.5f, 0), uLight);

            // Whole-model placement: yaw to face the heading, then translate to the entity's feet.
            var root = Matrix4.CreateRotationY(entity.Yaw) * Matrix4.CreateTranslation(pos);

            foreach (var (part, vao) in model.Parts)
            {
                var rotation = part.Rotation + PartRotation(part.Name, entity);
                var matrix = Matrix4.CreateRotationX(rotation.X) * Matrix4.CreateRotationY(rotation.Y) *
                             Matrix4.CreateRotationZ(rotation.Z) *
                             Matrix4.CreateTranslation(part.Pivot) * root;
                GL.UniformMatrix4(uModel, false, ref matrix);
                vao.Draw();
            }
        }

        private static void DrawItem(EntityItem item, WorldClient world, int uModel, int uLight)
        {
            if (item.Stack.IsEmpty) return;
            var block = item.Stack.Item?.GetBlock();
            if (block == null) return;
            var mesh = GetItemMesh(block.Id);
            if (mesh == null) return;

            var pos = item.RenderPosition;
            SetLight(world, pos + new Vector3(0, 0.25f, 0), uLight);

            var t = (float) AnimClock.Elapsed.TotalSeconds;
            var bob = 0.1f + 0.05f * MathF.Sin(t * 3f);
            var matrix = Matrix4.CreateScale(0.4f) * Matrix4.CreateRotationY(t * 2f) *
                         Matrix4.CreateTranslation(pos + new Vector3(0, 0.25f + bob, 0));
            GL.UniformMatrix4(uModel, false, ref matrix);
            mesh.Draw();
        }

        private static VertexArrayObject GetItemMesh(ushort blockId)
        {
            if (ItemMeshes.TryGetValue(blockId, out var mesh)) return mesh;

            var block = GameRegistry.GetBlock(blockId);
            if (block == BlockRegistry.BlockAir || block.Model == null) return ItemMeshes[blockId] = null;

            mesh = new VertexArrayObject();
            // Mesh the block's model centred at the origin in the void icon world (all faces, full light), the
            // same geometry the inventory icon uses; the entity shader replaces the baked light with uLight.
            ChunkMesher.AddBlockToVao(IconWorld.Instance, Vector3i.Zero, 0, 0, 0, block, mesh, mesh);
            mesh.Upload();
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

        private static void SetLight(WorldClient world, Vector3 worldPos, int uLight)
        {
            var bp = new Vector3i(BlockCoord(worldPos.X), BlockCoord(worldPos.Y), BlockCoord(worldPos.Z));
            var rgb = world.GetBlockLightLevel(bp).Vector3;
            var sky = world.GetSkyLight(bp);
            GL.Uniform4(uLight, new Vector4(Brightness(rgb.X), Brightness(rgb.Y), Brightness(rgb.Z), Brightness(sky)));
        }

        // Matches ChunkMesher's per-level falloff (Base^(15-level)) so entities sit at the same brightness as the
        // blocks around them.
        private static float Brightness(float level) => MathF.Pow(0.8f, MathF.Max(15f - level, 0f));

        private static int BlockCoord(float v) => (int) MathF.Floor(v + 0.5f);

        // Builds the six textured quads of one box into the VAO. Box coords are in blocks (relative to the part
        // pivot); UVs come from the classic Minecraft box-unwrap, normalized by the texture array's layer size.
        private static void AddBox(VertexArrayObject vao, ModelBox box, BlockTexture tex, int layerSize)
        {
            var f = box.From;
            var t = box.To;
            // Box pixel dimensions (1 block = 16 texels) drive the unwrap rectangle widths.
            var sx = (int) MathF.Round((t.X - f.X) * 16f);
            var sy = (int) MathF.Round((t.Y - f.Y) * 16f);
            var sz = (int) MathF.Round((t.Z - f.Z) * 16f);
            int u = box.TexU, v = box.TexV;

            // Left (-X) and Right (+X)
            Quad(vao, tex, layerSize, new Vector3(-1, 0, 0),
                new Vector3(f.X, t.Y, t.Z), new Vector3(f.X, t.Y, f.Z),
                new Vector3(f.X, f.Y, t.Z), new Vector3(f.X, f.Y, f.Z),
                u, v + sz, u + sz, v + sz + sy);
            Quad(vao, tex, layerSize, new Vector3(1, 0, 0),
                new Vector3(t.X, t.Y, f.Z), new Vector3(t.X, t.Y, t.Z),
                new Vector3(t.X, f.Y, f.Z), new Vector3(t.X, f.Y, t.Z),
                u + sz + sx, v + sz, u + 2 * sz + sx, v + sz + sy);
            // Front (+Z) and Back (-Z)
            Quad(vao, tex, layerSize, new Vector3(0, 0, 1),
                new Vector3(t.X, t.Y, t.Z), new Vector3(f.X, t.Y, t.Z),
                new Vector3(t.X, f.Y, t.Z), new Vector3(f.X, f.Y, t.Z),
                u + sz, v + sz, u + sz + sx, v + sz + sy);
            Quad(vao, tex, layerSize, new Vector3(0, 0, -1),
                new Vector3(f.X, t.Y, f.Z), new Vector3(t.X, t.Y, f.Z),
                new Vector3(f.X, f.Y, f.Z), new Vector3(t.X, f.Y, f.Z),
                u + 2 * sz + sx, v + sz, u + 2 * sz + 2 * sx, v + sz + sy);
            // Top (+Y) and Bottom (-Y)
            Quad(vao, tex, layerSize, new Vector3(0, 1, 0),
                new Vector3(t.X, t.Y, f.Z), new Vector3(f.X, t.Y, f.Z),
                new Vector3(t.X, t.Y, t.Z), new Vector3(f.X, t.Y, t.Z),
                u + sz, v, u + sz + sx, v + sz);
            Quad(vao, tex, layerSize, new Vector3(0, -1, 0),
                new Vector3(t.X, f.Y, t.Z), new Vector3(f.X, f.Y, t.Z),
                new Vector3(t.X, f.Y, f.Z), new Vector3(f.X, f.Y, f.Z),
                u + sz + sx, v, u + 2 * sz + sx, v + sz);
        }

        private static void Quad(VertexArrayObject vao, BlockTexture tex, int layerSize, Vector3 normal,
            Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br, int u0, int v0, int u1, int v1)
        {
            var baseVertex = vao.VertexCount;
            var n = new Vector4(normal, 0);
            var white = new Vector3(1);
            vao.Add(tl, TexCoord(tex, layerSize, u0, v0), n, white, Vector4.Zero);
            vao.Add(tr, TexCoord(tex, layerSize, u1, v0), n, white, Vector4.Zero);
            vao.Add(bl, TexCoord(tex, layerSize, u0, v1), n, white, Vector4.Zero);
            vao.Add(br, TexCoord(tex, layerSize, u1, v1), n, white, Vector4.Zero);
            vao.AddFace(baseVertex, false, Vector3.Zero);
        }

        private static Vector4 TexCoord(BlockTexture tex, int layerSize, int u, int v)
            => new Vector4((float) u / layerSize, (float) v / layerSize, tex.TextureId, tex.ArrayId);
    }
}

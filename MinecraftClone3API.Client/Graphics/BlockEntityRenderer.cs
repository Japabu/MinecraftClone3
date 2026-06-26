using System.Collections.Generic;
using System.Diagnostics;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Draws blocks that render as <em>block entities</em> (e.g. chests) — separate box models the chunk mesher
    /// skips (<see cref="Block.RendersAsBlockEntity"/>). Each block type's model is built once in
    /// <see cref="LoadModels"/> from its <see cref="Block.BlockEntityModelPath"/>/<see cref="Block.BlockEntityTexturePath"/>
    /// (reusing the entity box-model pipeline), then at draw time each loaded block-entity instance is found by
    /// scanning the sparse per-chunk block-data, placed in its cell and oriented by its stored facing. The same
    /// models back the inventory icon and first-person viewmodel for these blocks (see <see cref="GetModel"/>).
    /// A chest whose screen is open animates its lid up about the back hinge (see <see cref="SetChestOpen"/>).
    /// </summary>
    public static class BlockEntityRenderer
    {
        // Only block entities within this many chunks of the player are drawn (chests are small; far ones aren't
        // worth the per-chunk scan). Bounds the per-frame block-data sweep.
        private const int RenderChunkRadius = 8;

        // Seconds for a chest lid to swing fully open or closed.
        private const float LidOpenSeconds = 0.4f;

        private static readonly Dictionary<ushort, EntityRenderer.RenderModel> Models =
            new Dictionary<ushort, EntityRenderer.RenderModel>();

        // Chests whose screen is currently open on this client, and the per-chest lid open progress (0 closed ..
        // 1 open) eased toward each chest's target each frame. Keyed by world position. Driven locally, so it only
        // animates chests this client opens (a remote viewer opening one isn't reflected — see docs/known-issues).
        private static readonly HashSet<Vector3i> OpenChests = new HashSet<Vector3i>();
        private static readonly Dictionary<Vector3i, float> LidProgress = new Dictionary<Vector3i, float>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double _lastSeconds;

        /// <summary>Builds the box model for every registered block-entity block. Must run after plugin load and
        /// before the block-texture upload (alongside <see cref="EntityRenderer.LoadModels"/>), so the models'
        /// textures land in the arrays. Main-thread (GL) only.</summary>
        public static void LoadModels()
        {
            foreach (var block in GameRegistry.Blocks)
            {
                if (!block.RendersAsBlockEntity || block.BlockEntityModelPath == null) continue;
                Models[block.Id] = EntityRenderer.BuildModelFromPaths(block.BlockEntityModelPath, block.BlockEntityTexturePath);
            }

            // Flat (non-block) item sprites must also land in the texture arrays here (before the GPU upload) so
            // the held-item viewmodel can extrude and sample them with the entity shader.
            HeldItemMeshes.RegisterTextures();
        }

        /// <summary>The prebuilt block-entity model for a block id, or null if it isn't a block entity.</summary>
        internal static EntityRenderer.RenderModel GetModel(ushort blockId)
            => Models.TryGetValue(blockId, out var model) ? model : null;

        /// <summary>Marks the chest at <paramref name="pos"/> open or closed (called by the chest screen); the lid
        /// animates toward that state. Idempotent.</summary>
        public static void SetChestOpen(Vector3i pos, bool open)
        {
            if (open) OpenChests.Add(pos);
            else OpenChests.Remove(pos);
        }

        public static void Render(WorldClient world, Camera camera, Matrix4 projection)
        {
            if (Models.Count == 0) return;

            var now = Clock.Elapsed.TotalSeconds;
            var dt = (float) (now - _lastSeconds);
            _lastSeconds = now;
            if (dt < 0f) dt = 0f;
            if (dt > 0.1f) dt = 0.1f;

            EntityRenderer.BindShader(camera, projection, out var uModel, out var uLight, out _);
            RenderState.Set(new GlState {CullFace = false, DepthTest = true, DepthFunc = DepthFunction.Lequal});

            var centerChunk = WorldBase.ChunkInWorld(new Vector3i(
                (int) System.MathF.Floor(camera.Position.X),
                (int) System.MathF.Floor(camera.Position.Y),
                (int) System.MathF.Floor(camera.Position.Z)));

            foreach (var chunk in world.LoadedChunks.Values)
            {
                var d = chunk.Position - centerChunk;
                if (System.Math.Abs(d.X) > RenderChunkRadius || System.Math.Abs(d.Y) > RenderChunkRadius ||
                    System.Math.Abs(d.Z) > RenderChunkRadius)
                    continue;

                foreach (var local in chunk.BlockDataPositions)
                {
                    var id = chunk.GetBlock(local);
                    if (id == 0) continue;
                    var model = GetModel(id);
                    if (model == null) continue;

                    var block = GameRegistry.GetBlock(id);
                    var worldPos = chunk.Position * Chunk.Size + local;
                    DrawAt(model, block, world, worldPos, uModel, uLight, dt);
                }
            }

            RenderState.Set(new GlState {CullFace = true, DepthTest = true, DepthFunc = DepthFunction.Lequal});
        }

        // Places the model in the block's cell (its geometry is authored centred on x/z with its feet at y=0) and
        // orients it by the block's stored facing, lit by the block light at the cell centre. Chests with an open
        // (or closing) lid draw with the lid swung about its hinge.
        private static void DrawAt(EntityRenderer.RenderModel model, Block block, WorldClient world, Vector3i worldPos,
            int uModel, int uLight, float dt)
        {
            var centre = worldPos.ToVector3() + new Vector3(0.5f, 0.5f, 0.5f);
            EntityRenderer.SetLight(world, centre, uLight);

            var root = Matrix4.CreateRotationY(block.GetBlockEntityRotation(world, worldPos)) *
                       Matrix4.CreateTranslation(worldPos.X + 0.5f, worldPos.Y, worldPos.Z + 0.5f);

            var progress = AnimateLid(worldPos, dt);
            if (progress <= 0f)
            {
                EntityRenderer.DrawStaticParts(model, root, uModel);
                return;
            }

            // Minecraft's lid easing: angle = -(1-(1-p)^3) * 90°, hinged at the back-top edge so the latched front
            // lifts up.
            var inv = 1f - progress;
            var eased = 1f - inv * inv * inv;
            DrawChest(model, root, uModel, -eased * System.MathF.PI * 0.5f);
        }

        // Steps the chest's lid progress toward its open/closed target and returns it (0 closed .. 1 open).
        private static float AnimateLid(Vector3i pos, float dt)
        {
            var target = OpenChests.Contains(pos) ? 1f : 0f;
            LidProgress.TryGetValue(pos, out var p);
            var step = dt / LidOpenSeconds;
            if (p < target) p = System.MathF.Min(target, p + step);
            else if (p > target) p = System.MathF.Max(target, p - step);

            if (p <= 0f && target <= 0f)
            {
                LidProgress.Remove(pos);
                return 0f;
            }

            LidProgress[pos] = p;
            return p;
        }

        // Like EntityRenderer.DrawStaticParts, but rotates the "lid"/"lock" bones about the lid's hinge (its pivot,
        // the back-top edge) by lidAngle so the chest opens. Other parts draw at rest.
        private static void DrawChest(EntityRenderer.RenderModel model, Matrix4 root, int uModel, float lidAngle)
        {
            var hinge = Vector3.Zero;
            foreach (var (part, _) in model.Parts)
                if (part.Name == "lid") { hinge = part.Pivot; break; }
            var hingeRot = Matrix4.CreateTranslation(-hinge) *
                           Matrix4.CreateRotationX(lidAngle) *
                           Matrix4.CreateTranslation(hinge);

            foreach (var (part, vao) in model.Parts)
            {
                var local = Matrix4.CreateRotationX(part.Rotation.X) * Matrix4.CreateRotationY(part.Rotation.Y) *
                            Matrix4.CreateRotationZ(part.Rotation.Z) * Matrix4.CreateTranslation(part.Pivot);
                var matrix = part.Name == "lid" || part.Name == "lock" ? local * hingeRot * root : local * root;
                GL.UniformMatrix4(uModel, false, ref matrix);
                vao.Draw();
            }
        }
    }
}

using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Renders a block into a small off-screen texture as a Minecraft-style isometric icon, cached per
    /// block id. The block is meshed in the void <see cref="IconWorld"/> (all six faces, full light) and
    /// drawn with the <see cref="ClientResources.ItemIconShader"/> into a <see cref="TextureFramebuffer"/>.
    /// Lazy and main-thread only (every step here is a GL call); the inventory GUI calls
    /// <see cref="GetIcon"/> while drawing.
    /// </summary>
    public static class ItemIconRenderer
    {
        public const int Size = 64;

        private static readonly Dictionary<ushort, TextureFramebuffer> Cache = new Dictionary<ushort, TextureFramebuffer>();

        // Isometric camera: look down at the cube (centred on the origin, spanning [-0.5,0.5]) from the
        // top-front-right so the +Y/top, +Z/front and +X/right faces are all visible, matching MC's icons.
        private static readonly Matrix4 View =
            Matrix4.LookAt(new Vector3(1.2f, 1.05f, 1.2f), Vector3.Zero, Vector3.UnitY);
        private static readonly Matrix4 Projection =
            Matrix4.CreateOrthographicOffCenter(-0.9f, 0.9f, -0.9f, 0.9f, -10f, 10f);

        /// <summary>The icon texture for a block id (rendered and cached on first request).</summary>
        public static Texture GetIcon(ushort blockId)
        {
            if (Cache.TryGetValue(blockId, out var fbo)) return fbo.Texture;
            fbo = Render(GameRegistry.GetBlock(blockId));
            Cache[blockId] = fbo;
            return fbo.Texture;
        }

        private static TextureFramebuffer Render(Block block)
        {
            var fbo = new TextureFramebuffer(Size, Size, true);

            fbo.Bind();
            fbo.Clear(new Color4(0f, 0f, 0f, 0f));

            // Depth so the cube's near faces occlude the far ones; no face culling because non-full models
            // (stairs, etc.) rely on their back faces being visible.
            RenderState.Set(new GlState { DepthTest = true });

            var shader = ClientResources.ItemIconShader;
            shader.Bind();
            var view = View;
            var projection = Projection;
            GL.UniformMatrix4(shader.GetUniformLocation("uView"), false, ref view);
            GL.UniformMatrix4(shader.GetUniformLocation("uProjection"), false, ref projection);
            GL.Uniform1(shader.GetUniformLocation("uTextures16"), 0);
            GL.Uniform1(shader.GetUniformLocation("uTextures64"), 1);
            GL.Uniform1(shader.GetUniformLocation("uTextures256"), 2);
            GL.Uniform1(shader.GetUniformLocation("uTextures1024"), 3);

            GlTextureUploader.Bind();
            Samplers.BindBlockTextureSampler();

            var uModel = shader.GetUniformLocation("uModel");

            // Block entities (chests) have no chunk-mesh cube; draw their box model instead (same packed vertex
            // format, so the icon shader handles it). Use this single-output shader — NOT the entity shader,
            // whose 3 G-buffer outputs into this 1-attachment icon FBO render nothing on macOS.
            if (block.RendersAsBlockEntity && BlockEntityRenderer.GetModel(block.Id) is EntityRenderer.RenderModel beModel)
            {
                // The model is centred on x/z with its feet at y=0; drop it so it sits centred in the icon.
                EntityRenderer.DrawStaticParts(beModel, Matrix4.CreateTranslation(0f, -0.45f, 0f), uModel);
                fbo.Unbind(ClientResources.Window.FramebufferSize.X, ClientResources.Window.FramebufferSize.Y);
                return fbo;
            }

            var vao = new VertexArrayObject();
            ChunkMesher.AddBlockToVao(IconWorld.Instance, Vector3i.Zero, 0, 0, 0, block, vao, vao);
            vao.Upload();

            var identity = Matrix4.Identity;
            GL.UniformMatrix4(uModel, false, ref identity);
            vao.Draw();

            fbo.Unbind(ClientResources.Window.FramebufferSize.X, ClientResources.Window.FramebufferSize.Y);
            vao.Dispose();

            return fbo;
        }
    }
}

using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Client.Graphics
{
    public static class GuiRenderer
    {
        public static void DrawTexture(Texture texture, Rectangle rect, Rectangle? uvRect, bool gui = true)
            => DrawTexture(texture, rect, uvRect, Color4.White, gui);

        public static void DrawTexture(Texture texture, Rectangle rect, Rectangle? uvRect, Color4 color, bool gui = true)
        {
            //Convert pixel space to normalized coords (0)-(1)
            var r = new Vector4(rect.MinX, rect.MinY, rect.MaxX, rect.MaxY);

            var pixelSize = new Vector4(
                ScaledResolution.PixelSize.X, ScaledResolution.PixelSize.Y,
                ScaledResolution.PixelSize.X, ScaledResolution.PixelSize.Y);

            uvRect = uvRect ?? new Rectangle(0, 0, texture.Width, texture.Height);
            var uvrect = new Vector4((float) uvRect.Value.MinX / texture.Width, (float) uvRect.Value.MinY / texture.Height,
                (float) uvRect.Value.MaxX / texture.Width, (float) uvRect.Value.MaxY / texture.Height);

            if (gui)
                DrawTexture(texture, (ScaledResolution.GuiScale * r + new Vector4(ScaledResolution.GuiOffset.X,
                                          ScaledResolution.GuiOffset.Y, ScaledResolution.GuiOffset.X,
                                          ScaledResolution.GuiOffset.Y)) * pixelSize, uvrect, color);
            else
                DrawTexture(texture, r * pixelSize, uvrect, color);
        }

        public static void DrawTexture(Texture texture, Vector4 rect, Vector4 uvRect)
            => DrawTexture(texture, rect, uvRect, Color4.White);

        // FREEZE DIAG: set by GuiMainMenu when it sees a standing GL error, to attribute which step of the next
        // few GUI draws regenerates it. Capped by _probeTotalBudget so it can't spam the log.
        public static int ProbeArmed;
        private static int _probeTotalBudget = 48;

        public static void DrawTexture(Texture texture, Vector4 rect, Vector4 uvRect, Color4 color)
        {
            //Convert to clip space (-1)-(+1)
            rect = rect * 2 + new Vector4(-1);

            var diag = ProbeArmed > 0 && _probeTotalBudget > 0;
            if (diag) { ProbeArmed--; GL.GetError(); } // clear any pre-existing error so steps below attribute cleanly

            var shader = ClientResources.SpriteShader;
            shader.Bind();
            Probe(diag, "bind");

            // Uniform locations are queried by name rather than declared with explicit
            // layout(location=) qualifiers, which require GLSL 4.30 (macOS caps at 4.10).
            GL.Uniform4(shader.GetUniformLocation("uRect"), rect);
            GL.Uniform4(shader.GetUniformLocation("uUVRect"), uvRect);
            GL.Uniform4(shader.GetUniformLocation("uColor"), color);
            Probe(diag, "uniforms");

            texture.Bind(TextureUnit.Texture0);
            Probe(diag, "texBind");
            Samplers.BindGuiSampler(0);
            Probe(diag, "sampler");
            ClientResources.ScreenRectVao.Draw();
            if (diag && _probeTotalBudget > 0 && GL.GetError() != ErrorCode.NoError)
            {
                _probeTotalBudget--;
                GL.GetInteger(GetPName.CurrentProgram, out int prog);
                GL.GetInteger(GetPName.VertexArrayBinding, out int vao);
                GL.GetInteger(GetPName.ArrayBufferBinding, out int vbo);
                GL.GetInteger(GetPName.ElementArrayBufferBinding, out int ibo);
                GL.GetInteger(GetPName.ActiveTexture, out int activeTex);
                var srv = ClientResources.ScreenRectVao;
                var idxId = srv.DebugIndexBufferId;
                var idxAlive = GL.IsBuffer(idxId);
                Logger.Info($"FREEZE-DIAG draw FAIL: prog={prog} vao={vao}(srv={srv.DebugVaoId}) arrayBuf={vbo} elemBuf={ibo} " +
                            $"srvIndexBuf={idxId} srvIndexBufAlive={idxAlive} uploaded={srv.UploadedCount} " +
                            $"activeTex={activeTex - (int) TextureUnit.Texture0}");
            }
        }

        private static void Probe(bool diag, string step)
        {
            if (!diag) return;
            var e = GL.GetError();
            if (e != ErrorCode.NoError && _probeTotalBudget > 0)
            {
                _probeTotalBudget--;
                Logger.Info($"FREEZE-DIAG gui draw step '{step}' -> {e}");
            }
        }
    }
}
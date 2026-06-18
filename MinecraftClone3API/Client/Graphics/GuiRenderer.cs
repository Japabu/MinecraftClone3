using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Client.Graphics
{
    public static class GuiRenderer
    {
        public static void DrawTexture(Texture texture, Rectangle rect, Rectangle? uvRect, bool gui = true)
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
                                          ScaledResolution.GuiOffset.Y)) * pixelSize, uvrect);
            else
                DrawTexture(texture, r * pixelSize, uvrect);
        }

        public static void DrawTexture(Texture texture, Vector4 rect, Vector4 uvRect)
        {
            //Convert to clip space (-1)-(+1)
            rect = rect * 2 + new Vector4(-1);

            var shader = ClientResources.SpriteShader;
            shader.Bind();

            // Uniform locations are queried by name rather than declared with explicit
            // layout(location=) qualifiers, which require GLSL 4.30 (macOS caps at 4.10).
            GL.Uniform4(shader.GetUniformLocation("uRect"), rect);
            GL.Uniform4(shader.GetUniformLocation("uUVRect"), uvRect);

            texture.Bind(TextureUnit.Texture0);
            ClientResources.ScreenRectVao.Draw();
        }
    }
}
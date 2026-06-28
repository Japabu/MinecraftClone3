using MinecraftClone3API.Client;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Client.Graphics
{
    /// <summary>
    /// Immediate-looking sprite API for the GUI states. Each call converts pixel/GUI-space rectangles into
    /// the normalized 0..1 frame and appends a quad to <see cref="GuiBatch"/>; the frame conductor flushes
    /// the batch in one surface pass after the world tonemap (so HUD/menus draw on top). No GPU state, no
    /// per-call draw — the recording happens at frame end.
    /// </summary>
    public static class GuiRenderer
    {
        public static void DrawTexture(Texture texture, Rectangle rect, Rectangle? uvRect, bool gui = true)
            => DrawTexture(texture, rect, uvRect, new Vector4(1f, 1f, 1f, 1f), gui);

        /// <summary>Draws a texture across the whole framebuffer, cropping its source to the screen aspect
        /// (cover-fit) so a full-screen background fills any window aspect without letterboxing or stretching.</summary>
        public static void DrawCover(Texture texture)
        {
            int screenX = ClientResources.Width, screenY = ClientResources.Height;
            var textureAspect = (float) texture.Width / texture.Height;
            var screenAspect = (float) screenX / screenY;

            var w = texture.Width;
            var h = texture.Height;
            if (screenAspect > textureAspect) h = (int) (texture.Width / screenAspect);
            else w = (int) (texture.Height * screenAspect);

            var src = Rectangle.FromSize((texture.Width - w) / 2, (texture.Height - h) / 2, w, h);
            DrawTexture(texture, new Rectangle(0, 0, screenX, screenY), src, false);
        }

        public static void DrawTexture(Texture texture, Rectangle rect, Rectangle? uvRect, Vector4 color,
            bool gui = true, bool invert = false)
        {
            var r = new Vector4(rect.MinX, rect.MinY, rect.MaxX, rect.MaxY);

            var pixelSize = new Vector4(
                ScaledResolution.PixelSize.X, ScaledResolution.PixelSize.Y,
                ScaledResolution.PixelSize.X, ScaledResolution.PixelSize.Y);

            uvRect = uvRect ?? new Rectangle(0, 0, texture.Width, texture.Height);
            var uvrect = new Vector4((float)uvRect.Value.MinX / texture.Width, (float)uvRect.Value.MinY / texture.Height,
                (float)uvRect.Value.MaxX / texture.Width, (float)uvRect.Value.MaxY / texture.Height);

            if (gui)
                DrawTexture(texture, (ScaledResolution.GuiScale * r + new Vector4(ScaledResolution.GuiOffset.X,
                                          ScaledResolution.GuiOffset.Y, ScaledResolution.GuiOffset.X,
                                          ScaledResolution.GuiOffset.Y)) * pixelSize, uvrect, color, invert);
            else
                DrawTexture(texture, r * pixelSize, uvrect, color, invert);
        }

        public static void DrawTexture(Texture texture, Vector4 rect, Vector4 uvRect)
            => DrawTexture(texture, rect, uvRect, new Vector4(1f, 1f, 1f, 1f));

        public static void DrawTexture(Texture texture, Vector4 rect, Vector4 uvRect, Vector4 color, bool invert = false)
        {
            // Convert the normalized 0..1 rect to clip space (-1..+1); the sprite shader flips Y for the
            // GUI's top-left origin.
            var clip = rect * 2f + new Vector4(-1f, -1f, -1f, -1f);
            GuiBatch.Add(texture, clip, uvRect, color, invert);
        }
    }
}

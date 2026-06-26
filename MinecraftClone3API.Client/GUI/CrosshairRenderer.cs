using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// Draws the centred aiming crosshair from the official 1.20.2+ <c>crosshair.png</c> sprite (a 15x15
    /// image; a thin white plus when no resource pack is present). Uses Minecraft's inverting blend
    /// (one-minus-dst) so the reticle stays visible against any background. Mirrors <see cref="HotbarRenderer"/>.
    /// </summary>
    public static class CrosshairRenderer
    {
        private const int Size = 15;
        private const int Scale = 2;

        public static void Render()
        {
            var s = Size * Scale;
            var x = ((int) ScaledResolution.GuiResolution.X - s) / 2;
            var y = ((int) ScaledResolution.GuiResolution.Y - s) / 2;

            var crosshair = GuiAssets.Get(GuiAssets.Crosshair);
            if (crosshair != null)
            {
                GuiRenderer.DrawTexture(crosshair, Rectangle.FromSize(x, y, s, s), null);
                return;
            }

            DrawPlaceholder(x, y, s);
        }

        private static void DrawPlaceholder(int x, int y, int s)
        {
            var thickness = Scale;
            var cx = x + (s - thickness) / 2;
            var cy = y + (s - thickness) / 2;
            GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                Rectangle.FromSize(cx, y, thickness, s), null, new Vector4D<float>(1f,1f,1f,1f));
            GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                Rectangle.FromSize(x, cy, s, thickness), null, new Vector4D<float>(1f,1f,1f,1f));
        }
    }
}

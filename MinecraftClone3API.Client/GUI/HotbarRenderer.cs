using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// Draws the always-on hotbar HUD: the official <c>widgets.png</c> hotbar strip plus selection cursor
    /// (or placeholder boxes when no resource pack is present), with each held block's isometric icon.
    /// </summary>
    public static class HotbarRenderer
    {
        // widgets.png layout (pixels): the hotbar strip is the 182x22 region at the top-left, the selection
        // cursor the 24x24 region just below it. A slot is 20px wide; the first slot's 16x16 icon sits at (3,3).
        private const int BarWidth = 182;
        private const int BarHeight = 22;
        private const int SlotStride = 20;
        private const int IconInset = 3;
        private const int IconSize = 16;
        private const int CursorSize = 24;

        // Scale the (small) native widget up into the 960x540 GUI space.
        private const int Scale = 2;

        public static void Render(Inventory inventory)
        {
            RenderState.Set(new GlState
            {
                Blend = true,
                BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            });

            var barW = BarWidth * Scale;
            var barH = BarHeight * Scale;
            var x0 = ((int) ScaledResolution.GuiResolution.X - barW) / 2;
            var y0 = (int) ScaledResolution.GuiResolution.Y - barH - 2;

            var widgets = GuiAssets.Get(GuiAssets.Widgets);
            if (widgets != null)
            {
                GuiRenderer.DrawTexture(widgets, Rectangle.FromSize(x0, y0, barW, barH),
                    new Rectangle(0, 0, BarWidth, BarHeight));

                var sel = inventory.SelectedHotbar;
                var cx = x0 + (sel * SlotStride - 1) * Scale;
                var cy = y0 - 1 * Scale;
                GuiRenderer.DrawTexture(widgets, Rectangle.FromSize(cx, cy, CursorSize * Scale, CursorSize * Scale),
                    new Rectangle(0, BarHeight, CursorSize, BarHeight + CursorSize));
            }
            else
            {
                DrawPlaceholder(x0, y0, barW, barH, inventory.SelectedHotbar);
            }

            for (var i = 0; i < Inventory.HotbarSize; i++)
            {
                var ix = x0 + (IconInset + i * SlotStride) * Scale;
                var iy = y0 + IconInset * Scale;
                ItemStackRenderer.Draw(inventory.Slots[i], Rectangle.FromSize(ix, iy, IconSize * Scale, IconSize * Scale));
            }
        }

        private static void DrawPlaceholder(int x0, int y0, int barW, int barH, int selected)
        {
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, Rectangle.FromSize(x0, y0, barW, barH), null,
                new Color4(0f, 0f, 0f, 0.6f));
            for (var i = 0; i < Inventory.HotbarSize; i++)
            {
                var sx = x0 + (1 + i * SlotStride) * Scale;
                var color = i == selected ? new Color4(1f, 1f, 1f, 0.9f) : new Color4(0.6f, 0.6f, 0.6f, 0.6f);
                GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                    Rectangle.FromSize(sx, y0 + 1 * Scale, (SlotStride - 2) * Scale, (BarHeight - 2) * Scale), null, color);
            }
        }
    }
}

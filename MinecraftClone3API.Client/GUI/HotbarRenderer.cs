using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// Draws the always-on hotbar HUD: the official <c>widgets.png</c> hotbar strip plus selection cursor
    /// (or placeholder boxes when no resource pack is present), with each held block's isometric icon.
    /// </summary>
    public static class HotbarRenderer
    {
        // Modern HUD sprite layout: hotbar.png is the whole 182x22 bar, hotbar_selection.png the 24x23 cursor.
        // A slot is 20px wide; the first slot's 16x16 icon sits at (3,3).
        private const int BarWidth = 182;
        private const int BarHeight = 22;
        private const int SlotStride = 20;
        private const int IconInset = 3;
        private const int IconSize = 16;
        private const int CursorWidth = 24;
        private const int CursorHeight = 23;

        // Scale the (small) native widget up into the 960x540 GUI space.
        private const int Scale = 2;

        public static void Render(Inventory inventory)
        {
            var barW = BarWidth * Scale;
            var barH = BarHeight * Scale;
            var x0 = ((int) ScaledResolution.GuiResolution.X - barW) / 2;
            var y0 = (int) ScaledResolution.GuiResolution.Y - barH - 2;

            var bar = GuiAssets.Get(GuiAssets.Hotbar);
            if (bar != null)
            {
                GuiRenderer.DrawTexture(bar, Rectangle.FromSize(x0, y0, barW, barH), null);

                var selection = GuiAssets.Get(GuiAssets.HotbarSelection);
                if (selection != null)
                {
                    var sel = inventory.SelectedHotbar;
                    var cx = x0 + (sel * SlotStride - 1) * Scale;
                    var cy = y0 - 1 * Scale;
                    GuiRenderer.DrawTexture(selection,
                        Rectangle.FromSize(cx, cy, CursorWidth * Scale, CursorHeight * Scale), null);
                }
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
                new Vector4D<float>(0f, 0f, 0f, 0.6f));
            for (var i = 0; i < Inventory.HotbarSize; i++)
            {
                var sx = x0 + (1 + i * SlotStride) * Scale;
                var color = i == selected ? new Vector4D<float>(1f, 1f, 1f, 0.9f) : new Vector4D<float>(0.6f, 0.6f, 0.6f, 0.6f);
                GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                    Rectangle.FromSize(sx, y0 + 1 * Scale, (SlotStride - 2) * Scale, (BarHeight - 2) * Scale), null, color);
            }
        }
    }
}

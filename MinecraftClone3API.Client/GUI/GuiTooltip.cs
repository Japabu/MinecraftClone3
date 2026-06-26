using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>Draws the Minecraft-style item-name tooltip — the localized item name on a translucent panel
    /// next to the cursor — used by every inventory/crafting screen when hovering a non-empty slot.</summary>
    public static class GuiTooltip
    {
        private const int Scale = 2;
        private const int Pad = 4;

        public static void Draw(ItemStack stack, Vector2D<float> mouseGuiPos)
        {
            if (stack.IsEmpty) return;
            var item = stack.Item;
            if (item == null) return;

            var name = item.GetName();
            var w = Font.MeasureWidth(name, Scale);
            var h = Font.LineHeight(Scale);
            var x = (int) mouseGuiPos.X + 10;
            var y = (int) mouseGuiPos.Y - 10;

            GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                Rectangle.FromSize(x - Pad, y - Pad, w + Pad * 2, h + Pad * 2), null, new Vector4D<float>(0.05f, 0.0f, 0.1f, 0.9f));
            Font.DrawString(name, x, y, Scale, new Vector4D<float>(1f,1f,1f,1f));
        }
    }
}

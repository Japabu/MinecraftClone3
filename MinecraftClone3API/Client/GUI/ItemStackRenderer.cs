using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>Draws an <see cref="ItemStack"/> in a GUI slot: the block's cached isometric icon (see
    /// <see cref="ItemIconRenderer"/>) plus a stack-count label when the count is above one.</summary>
    public static class ItemStackRenderer
    {
        // The icon framebuffer is rendered with GL's bottom-left origin, so it reads upside-down through the
        // GUI's top-left sampling; flip V (MinY/MaxY swapped) to present it upright.
        private static readonly Rectangle FlipV = new Rectangle(0, ItemIconRenderer.Size, ItemIconRenderer.Size, 0);

        public static void Draw(ItemStack stack, Rectangle rect)
        {
            if (stack.IsEmpty) return;

            // GetIcon may render into its own framebuffer (depth on, blend off); restore alpha blending
            // before drawing the icon so its transparent background doesn't overwrite the GUI as black.
            var icon = ItemIconRenderer.GetIcon(stack.BlockId);
            RenderState.Set(new GlState
            {
                Blend = true,
                BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            });
            GuiRenderer.DrawTexture(icon, rect, FlipV);

            if (stack.Count > 1)
            {
                var text = stack.Count.ToString();
                var scale = 1;
                var tx = rect.MaxX - Font.MeasureWidth(text, scale) - 1;
                var ty = rect.MaxY - Font.LineHeight(scale) - 1;
                Font.DrawString(text, tx, ty, scale, Color4.White);
            }
        }
    }
}

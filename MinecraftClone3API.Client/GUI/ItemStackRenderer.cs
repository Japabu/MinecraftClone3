using System.Collections.Generic;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>Draws an <see cref="ItemStack"/> in a GUI slot: a block item's cached 3D isometric icon (see
    /// <see cref="ItemIconRenderer"/>) or a standalone item's flat 2D sprite, plus a stack-count label when the
    /// count is above one.</summary>
    public static class ItemStackRenderer
    {
        // The icon framebuffer is rendered with GL's bottom-left origin, so it reads upside-down through the
        // GUI's top-left sampling; flip V (MinY/MaxY swapped) to present it upright.
        private static readonly Rectangle FlipV = new Rectangle(0, ItemIconRenderer.Size, ItemIconRenderer.Size, 0);

        // Lazily-loaded 2D sprites for non-block items, cached by item id (null = no resource pack provides it).
        private static readonly Dictionary<ushort, Texture> Sprites = new Dictionary<ushort, Texture>();

        public static void Draw(ItemStack stack, Rectangle rect)
        {
            if (stack.IsEmpty) return;
            var item = stack.Item;
            if (item == null) return;

            if (item is ItemBlock blockItem)
            {
                // GetIcon may render into its own framebuffer (depth on, blend off); restore alpha blending
                // afterwards so the icon's transparent background doesn't overwrite the GUI as black.
                var icon = ItemIconRenderer.GetIcon(blockItem.Block.Id);
                SetBlend();
                GuiRenderer.DrawTexture(icon, rect, FlipV);
            }
            else
            {
                var sprite = Sprite(item);
                SetBlend();
                if (sprite != null) GuiRenderer.DrawTexture(sprite, rect, null);
                else GuiRenderer.DrawTexture(ClientResources.WhitePixel, rect, null, new Color4(0.7f, 0.7f, 0.7f, 1f));
            }

            if (stack.Count > 1)
            {
                var text = stack.Count.ToString();
                var scale = 1;
                var tx = rect.MaxX - Font.MeasureWidth(text, scale) - 1;
                var ty = rect.MaxY - Font.LineHeight(scale) - 1;
                Font.DrawString(text, tx, ty, scale, Color4.White);
            }
        }

        private static void SetBlend() => RenderState.Set(new GlState
        {
            Blend = true,
            BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
        });

        private static Texture Sprite(Item item)
        {
            if (Sprites.TryGetValue(item.Id, out var tex)) return tex;
            tex = item.TexturePath != null && ResourceReader.Exists(item.TexturePath)
                ? GlResources.ReadTexture(item.TexturePath)
                : null;
            Sprites[item.Id] = tex;
            return tex;
        }
    }
}

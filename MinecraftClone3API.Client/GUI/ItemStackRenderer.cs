using System.Collections.Generic;
using System.Linq;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>Draws an <see cref="ItemStack"/> in a GUI slot: a block item's cached 3D isometric icon (see
    /// <see cref="ItemIconRenderer"/>) or a standalone item's flat 2D sprite, plus a stack-count label when the
    /// count is above one.</summary>
    public static class ItemStackRenderer
    {
        // Lazily-loaded 2D sprites for non-block items, cached by item id (null = no resource pack provides it).
        private static readonly Dictionary<ushort, Texture> Sprites = new Dictionary<ushort, Texture>();

        public static void Draw(ItemStack stack, Rectangle rect)
        {
            if (stack.IsEmpty) return;
            var item = stack.Item;
            if (item == null) return;

            // A flat sprite wins when one is available — a standalone item's TexturePath, or a block that opts
            // into a flat icon (torch/ladder/flowers via ItemSpriteTexture). A plain block falls to its 3D icon.
            var sprite = Sprite(item);
            if (sprite != null)
                GuiRenderer.DrawTexture(sprite, rect, null);
            else if (item is ItemBlock blockItem)
                GuiRenderer.DrawTexture(ItemIconRenderer.GetIcon(blockItem.Block.Id), rect, null);
            else
                GuiRenderer.DrawTexture(ClientResources.WhitePixel, rect, null, new Vector4D<float>(0.7f, 0.7f, 0.7f, 1f));

            if (stack.Count > 1)
            {
                var text = stack.Count.ToString();
                var scale = 1;
                var tx = rect.MaxX - Font.MeasureWidth(text, scale) - 1;
                var ty = rect.MaxY - Font.LineHeight(scale) - 1;
                Font.DrawString(text, tx, ty, scale, new Vector4D<float>(1f,1f,1f,1f));
            }
        }

        private static Texture Sprite(Item item)
        {
            if (Sprites.TryGetValue(item.Id, out var tex)) return tex;
            var path = SpritePath(item);
            tex = path != null ? GlResources.ReadTexture(path) : null;
            Sprites[item.Id] = tex;
            return tex;
        }

        // The PNG to blit for a flat icon: a standalone item carries a full PNG path; a block opting into a flat
        // icon carries a texture LOCATION (minecraft:block/torch), resolved the same way block textures are.
        private static string SpritePath(Item item)
        {
            if (item.TexturePath != null)
                return ResourceReader.Exists(item.TexturePath) ? item.TexturePath : null;
            var loc = (item as ItemBlock)?.Block.ItemSpriteTexture;
            return loc == null ? null : BlockModel.GetRelativePaths(loc, loc, ".png").FirstOrDefault(ResourceReader.Exists);
        }
    }
}

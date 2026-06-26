using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// Draws the survival HUD above the hotbar: a row of hearts (health) on the left and a row of food icons
    /// (hunger) on the right, from the official 1.20.2+ HUD sprite PNGs (each a 9x9 image; coloured placeholders
    /// when no resource pack is present). Only shown in survival mode. Mirrors <see cref="HotbarRenderer"/>.
    /// </summary>
    public static class SurvivalHud
    {
        private const int Scale = 2;
        private const int IconSize = 9 * Scale;   // 18
        private const int IconStep = 8 * Scale;   // 16 (icons overlap by 1 native px, like Minecraft)
        private const int Icons = 10;             // 10 hearts / 10 food = 20 health / 20 hunger

        public static void Render(WorldClient world)
        {
            if (world.GameMode != GameMode.Survival || !world.StatsReceived) return;

            RenderState.Set(new GlState
            {
                Blend = true,
                BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            });

            // Sit the two rows just above the hotbar, aligned to its left/right edges.
            var barW = 182 * Scale;
            var x0 = ((int) ScaledResolution.GuiResolution.X - barW) / 2;
            var hotbarTop = (int) ScaledResolution.GuiResolution.Y - 22 * Scale - 2;
            var y = hotbarTop - IconSize - 2;

            for (var i = 0; i < Icons; i++)
            {
                var remaining = world.Health - i * 2;
                DrawIcon(x0 + i * IconStep, y, remaining, true);
            }

            var hungerRight = x0 + barW - IconSize;
            for (var i = 0; i < Icons; i++)
            {
                var remaining = world.Hunger - i * 2;
                DrawIcon(hungerRight - i * IconStep, y, remaining, false);
            }
        }

        private static void DrawIcon(int x, int y, float remaining, bool heart)
        {
            var dest = Rectangle.FromSize(x, y, IconSize, IconSize);

            var background = GuiAssets.Get(heart ? GuiAssets.HeartContainer : GuiAssets.FoodEmpty);
            if (background == null)
            {
                DrawPlaceholder(dest, remaining, heart);
                return;
            }

            GuiRenderer.DrawTexture(background, dest, null);
            if (remaining >= 2f)
                GuiRenderer.DrawTexture(GuiAssets.Get(heart ? GuiAssets.HeartFull : GuiAssets.FoodFull), dest, null);
            else if (remaining >= 1f)
                GuiRenderer.DrawTexture(GuiAssets.Get(heart ? GuiAssets.HeartHalf : GuiAssets.FoodHalf), dest, null);
        }

        private static void DrawPlaceholder(Rectangle dest, float remaining, bool heart)
        {
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, dest, null, new Color4(0f, 0f, 0f, 0.4f));
            if (remaining < 1f) return;

            var full = remaining >= 2f;
            var color = heart ? new Color4(0.85f, 0.1f, 0.1f, 1f) : new Color4(0.6f, 0.4f, 0.15f, 1f);
            var w = full ? IconSize - 4 : (IconSize - 4) / 2;
            GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                Rectangle.FromSize(dest.MinX + 2, dest.MinY + 2, w, IconSize - 4), null, color);
        }
    }
}

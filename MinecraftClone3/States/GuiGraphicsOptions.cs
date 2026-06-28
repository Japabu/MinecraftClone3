using System;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using Silk.NET.Maths;
using Silk.NET.Input;

namespace MinecraftClone3.States
{
    /// <summary>
    /// Graphics options screen. Shown as an overlay over the parent <see cref="GuiOptions"/> screen, so closing
    /// it via Done/Escape reveals that screen again. Each control mutates the
    /// persisted <see cref="GraphicsSettings"/>; the renderer/player/world read those values live, so changes
    /// (render distance, FOV, brightness, …) take effect immediately with no reload.
    /// </summary>
    internal class GuiGraphicsOptions : GuiBase
    {
        private const int RowWidth = 320;
        private const int RowHeight = 36;
        private const int RowGap = 8;
        private const int TitleScale = 3;
        private const int TitleY = 36;
        private const string Title = "Graphics Options";

        public GuiGraphicsOptions()
        {
            ClientResources.Input.CursorMode = CursorMode.Normal;

            var x = ((int) ScaledResolution.GuiResolution.X - RowWidth) / 2;
            var y0 = 76;
            var step = RowHeight + RowGap;
            var row = 0;

            GuiButton vsync = null;
            vsync = new GuiButton(Row(x, y0, step, row++), VSyncLabel(), () =>
            {
                GraphicsSettings.VSync = NextVSync(GraphicsSettings.VSync);
                vsync.Label = VSyncLabel();
            });
            Elements.Add(vsync);

            GuiButton shadows = null;
            shadows = new GuiButton(Row(x, y0, step, row++), ShadowLabel(), () =>
            {
                GraphicsSettings.ShadowQuality = NextShadow(GraphicsSettings.ShadowQuality);
                shadows.Label = ShadowLabel();
            });
            Elements.Add(shadows);

            GuiButton fullscreen = null;
            fullscreen = new GuiButton(Row(x, y0, step, row++), FullscreenLabel(), () =>
            {
                GraphicsSettings.Fullscreen = !GraphicsSettings.Fullscreen;
                fullscreen.Label = FullscreenLabel();
            });
            Elements.Add(fullscreen);

            Elements.Add(new GuiSlider(Row(x, y0, step, row++), "Render Distance",
                GraphicsSettings.MinRenderDistanceChunks, GraphicsSettings.MaxRenderDistanceChunks, 1f,
                GraphicsSettings.RenderDistanceChunks,
                v => GraphicsSettings.RenderDistanceChunks = (int) v,
                v => (int) v + " chunks"));

            Elements.Add(new GuiSlider(Row(x, y0, step, row++), "FOV",
                GraphicsSettings.MinFov, GraphicsSettings.MaxFov, 5f, GraphicsSettings.Fov,
                v => GraphicsSettings.Fov = v,
                v => ((int) v).ToString()));

            Elements.Add(new GuiSlider(Row(x, y0, step, row++), "Sensitivity",
                GraphicsSettings.MinMouseSensitivity, GraphicsSettings.MaxMouseSensitivity, 0.0005f,
                GraphicsSettings.MouseSensitivity,
                v => GraphicsSettings.MouseSensitivity = v,
                v => Percent(v, GraphicsSettings.MinMouseSensitivity, GraphicsSettings.MaxMouseSensitivity)));

            Elements.Add(new GuiSlider(Row(x, y0, step, row++), "Brightness",
                GraphicsSettings.MinBrightness, GraphicsSettings.MaxBrightness, 0.01f, GraphicsSettings.Brightness,
                v => GraphicsSettings.Brightness = v,
                v => Percent(v, GraphicsSettings.MinBrightness, GraphicsSettings.MaxBrightness)));

            Elements.Add(new GuiSlider(Row(x, y0, step, row++), "LOD Quality",
                GraphicsSettings.MinLodHorizonQuality, GraphicsSettings.MaxLodHorizonQuality, 0.25f,
                GraphicsSettings.LodHorizonQuality,
                v => GraphicsSettings.LodHorizonQuality = v,
                v => (int) Math.Round(v * 100) + "%"));

            Elements.Add(new GuiSlider(Row(x, y0, step, row++), "LOD Horizon",
                GraphicsSettings.MinLodHorizonChunks, GraphicsSettings.MaxLodHorizonChunks, 2f,
                GraphicsSettings.LodHorizonChunks,
                v => GraphicsSettings.LodHorizonChunks = (int) v,
                v => (int) v == 0 ? "Off" : (int) v + " chunks"));

            Elements.Add(new GuiButton(Row(x, y0, step, row), "Done", () => IsDead = true));
        }

        private static Rectangle Row(int x, int y0, int step, int row)
            => Rectangle.FromSize(x, y0 + row * step, RowWidth, RowHeight);

        private static string Percent(float value, float min, float max)
            => (int) Math.Round((value - min) / (max - min) * 100) + "%";

        private static string VSyncLabel() => "VSync: " + VSyncName(GraphicsSettings.VSync);

        private static string VSyncName(VSyncMode mode)
        {
            switch (mode)
            {
                case VSyncMode.Off: return "Off";
                case VSyncMode.On: return "On";
                case VSyncMode.Adaptive: return "Adaptive";
                default: return mode.ToString();
            }
        }

        private static VSyncMode NextVSync(VSyncMode mode)
        {
            switch (mode)
            {
                case VSyncMode.Off: return VSyncMode.On;
                case VSyncMode.On: return VSyncMode.Adaptive;
                default: return VSyncMode.Off;
            }
        }

        private static string ShadowLabel() => "Shadows: " + GraphicsSettings.ShadowQuality;

        private static ShadowQuality NextShadow(ShadowQuality quality)
        {
            switch (quality)
            {
                case ShadowQuality.Off: return ShadowQuality.Low;
                case ShadowQuality.Low: return ShadowQuality.Medium;
                case ShadowQuality.Medium: return ShadowQuality.High;
                default: return ShadowQuality.Off;
            }
        }

        private static string FullscreenLabel() => "Fullscreen: " + (GraphicsSettings.Fullscreen ? "On" : "Off");

        public override void OnKeyDown(Key key)
        {
            if (key == Key.Escape)
                IsDead = true;
        }

        public override void Render()
        {
            var screen = new Vector2D<int>(ClientResources.Width, ClientResources.Height);
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, new Rectangle(0, 0, screen.X, screen.Y), null,
                new Vector4D<float>(0f, 0f, 0f, 0.7f), false);

            var width = (int) ScaledResolution.GuiResolution.X;
            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, TitleY, TitleScale, new Vector4D<float>(1f,1f,1f,1f));

            base.Render();
        }
    }
}

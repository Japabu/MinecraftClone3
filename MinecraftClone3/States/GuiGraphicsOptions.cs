using System;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3.States
{
    /// <summary>
    /// Graphics options screen. Shown as an overlay over whatever opened it (the main menu state or the
    /// pause-menu overlay), so closing it via Done/Escape reveals that screen again. Each control mutates the
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

        private readonly GameWindow _window;

        public GuiGraphicsOptions(GameWindow window)
        {
            _window = window;
            _window.CursorState = CursorState.Normal;

            var x = ((int) ScaledResolution.GuiResolution.X - RowWidth) / 2;
            var y0 = 92;
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

        public override void Update(bool focused)
        {
            base.Update(focused);
            if (focused && _window.KeyboardState.IsKeyPressed(Keys.Escape))
                IsDead = true;
        }

        public override void Render()
        {
            RenderState.Set(new GlState
            {
                Blend = true,
                BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            });

            var screen = _window.FramebufferSize;
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, new Rectangle(0, 0, screen.X, screen.Y), null,
                new Color4(0f, 0f, 0f, 0.7f), false);

            var width = (int) ScaledResolution.GuiResolution.X;
            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, TitleY, TitleScale, Color4.White);

            base.Render();
        }
    }
}

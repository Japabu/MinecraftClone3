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
    /// pause-menu overlay), so closing it via Done/Escape reveals that screen again. Each option button
    /// mutates the persisted <see cref="GraphicsSettings"/> and refreshes its own label.
    /// </summary>
    internal class GuiGraphicsOptions : GuiBase
    {
        private const int ButtonWidth = 280;
        private const int ButtonHeight = 40;
        private const int ButtonGap = 12;
        private const int TitleScale = 3;
        private const string Title = "Graphics Options";

        private readonly GameWindow _window;

        public GuiGraphicsOptions(GameWindow window)
        {
            _window = window;
            _window.CursorState = CursorState.Normal;

            var x = ((int) ScaledResolution.GuiResolution.X - ButtonWidth) / 2;
            var y = (int) ScaledResolution.GuiResolution.Y / 2 - (4 * ButtonHeight + 3 * ButtonGap) / 2;
            var step = ButtonHeight + ButtonGap;

            GuiButton vsync = null;
            vsync = new GuiButton(Rectangle.FromSize(x, y, ButtonWidth, ButtonHeight), VSyncLabel(), () =>
            {
                GraphicsSettings.VSync = NextVSync(GraphicsSettings.VSync);
                vsync.Label = VSyncLabel();
            });
            Elements.Add(vsync);

            GuiButton shadows = null;
            shadows = new GuiButton(Rectangle.FromSize(x, y + step, ButtonWidth, ButtonHeight), ShadowsLabel(), () =>
            {
                GraphicsSettings.ShadowQuality = NextShadowQuality(GraphicsSettings.ShadowQuality);
                shadows.Label = ShadowsLabel();
            });
            Elements.Add(shadows);

            GuiButton fullscreen = null;
            fullscreen = new GuiButton(Rectangle.FromSize(x, y + 2 * step, ButtonWidth, ButtonHeight), FullscreenLabel(),
                () =>
                {
                    GraphicsSettings.Fullscreen = !GraphicsSettings.Fullscreen;
                    fullscreen.Label = FullscreenLabel();
                });
            Elements.Add(fullscreen);

            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + 3 * step, ButtonWidth, ButtonHeight), "Done",
                () => IsDead = true));
        }

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

        private static ShadowQuality NextShadowQuality(ShadowQuality quality)
        {
            switch (quality)
            {
                case ShadowQuality.Off: return ShadowQuality.Low;
                case ShadowQuality.Low: return ShadowQuality.Medium;
                case ShadowQuality.Medium: return ShadowQuality.High;
                default: return ShadowQuality.Off;
            }
        }

        private static string ShadowsLabel() => "Shadows: " + GraphicsSettings.ShadowQuality;
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
            var height = (int) ScaledResolution.GuiResolution.Y;
            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, height / 4, TitleScale, Color4.White);

            base.Render();
        }
    }
}

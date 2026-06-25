using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
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
    /// Top-level options screen. An overlay over whatever opened it (the main menu state or the pause-menu
    /// overlay); its buttons open the per-category sub-screens (<see cref="GuiGraphicsOptions"/>,
    /// <see cref="GuiControls"/>) as further overlays, and Done/Escape reveals the opener again.
    /// </summary>
    internal class GuiOptions : GuiBase
    {
        private const int ButtonWidth = 200;
        private const int ButtonHeight = 40;
        private const int ButtonGap = 12;
        private const int TitleScale = 3;
        private const string Title = "Options";

        private readonly GameWindow _window;

        public GuiOptions(GameWindow window)
        {
            _window = window;
            _window.CursorState = CursorState.Normal;

            var x = ((int) ScaledResolution.GuiResolution.X - ButtonWidth) / 2;
            var y = (int) ScaledResolution.GuiResolution.Y / 2 - (3 * ButtonHeight + 2 * ButtonGap) / 2;
            var step = ButtonHeight + ButtonGap;

            Elements.Add(new GuiButton(Rectangle.FromSize(x, y, ButtonWidth, ButtonHeight), "Graphics...",
                () => StateEngine.AddOverlay(new GuiGraphicsOptions(_window))));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + step, ButtonWidth, ButtonHeight), "Controls...",
                () => StateEngine.AddOverlay(new GuiControls(_window))));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + 2 * step, ButtonWidth, ButtonHeight), "Done",
                () => IsDead = true));
        }

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

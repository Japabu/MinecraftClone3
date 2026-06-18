using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3.States
{
    internal class GuiPauseMenu : GuiBase
    {
        private const int ButtonWidth = 200;
        private const int ButtonHeight = 40;
        private const int ButtonGap = 12;
        private const int TitleScale = 3;
        private const string Title = "Game Menu";

        private readonly GameWindow _window;

        public GuiPauseMenu(GameWindow window)
        {
            _window = window;
            _window.CursorState = CursorState.Normal;

            var x = ((int) ScaledResolution.GuiResolution.X - ButtonWidth) / 2;
            var y = (int) ScaledResolution.GuiResolution.Y / 2 - (3 * ButtonHeight + 2 * ButtonGap) / 2;
            var step = ButtonHeight + ButtonGap;

            Elements.Add(new GuiButton(Rectangle.FromSize(x, y, ButtonWidth, ButtonHeight), "Back to Game", Close));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + step, ButtonWidth, ButtonHeight), "Options", null)
                {Enabled = false});
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + 2 * step, ButtonWidth, ButtonHeight),
                "Save and Quit to Title", () => StateEngine.ReplaceState(new GuiMainMenu(_window))));
        }

        public override void Update(bool focused)
        {
            base.Update(focused);
            if (focused && _window.KeyboardState.IsKeyPressed(Keys.Escape))
                Close();
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
                new Color4(0f, 0f, 0f, 0.5f), false);

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, height / 4, TitleScale, Color4.White);

            base.Render();
        }

        private void Close()
        {
            _window.CursorState = CursorState.Grabbed;
            PlayerController.ResetMouse();
            IsDead = true;
        }
    }
}

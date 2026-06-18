using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace MinecraftClone3.States
{
    internal class GuiMainMenu : GuiBase
    {
        private const int ButtonWidth = 200;
        private const int ButtonHeight = 40;
        private const int ButtonGap = 12;
        private const int TitleScale = 4;
        private const string Title = "MinecraftClone3";

        private static Texture _background;

        private readonly GameWindow _window;

        public GuiMainMenu(GameWindow window)
        {
            _window = window;
            _window.CursorState = CursorState.Normal;

            if (_background == null)
                _background = ResourceReader.ReadTexture("System/Textures/Gui/ResourceLoadingBackground.png");

            var x = ((int) ScaledResolution.GuiResolution.X - ButtonWidth) / 2;
            var y = (int) ScaledResolution.GuiResolution.Y / 2 - (4 * ButtonHeight + 3 * ButtonGap) / 2;
            var step = ButtonHeight + ButtonGap;

            Elements.Add(new GuiButton(Rectangle.FromSize(x, y, ButtonWidth, ButtonHeight), "Singleplayer",
                () => StateEngine.ReplaceState(new StateWorld(_window, multiplayer: false))));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + step, ButtonWidth, ButtonHeight), "Multiplayer",
                () => StateEngine.ReplaceState(new StateWorld(_window, multiplayer: true))));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + 2 * step, ButtonWidth, ButtonHeight), "Options", null)
                {Enabled = false});
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + 3 * step, ButtonWidth, ButtonHeight), "Quit Game",
                () => _window.Close()));
        }

        public override void Update(bool focused) => base.Update(focused);

        public override void Render()
        {
            RenderState.Set(new GlState
            {
                Blend = true,
                BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            });

            var screen = _window.FramebufferSize;
            GuiRenderer.DrawTexture(_background, new Rectangle(0, 0, screen.X, screen.Y), CoverSource(_background, screen),
                false);

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, height / 6, TitleScale, Color4.White);

            base.Render();
        }

        /// <summary>
        /// A centered source rectangle that crops <paramref name="texture"/> to <paramref name="screen"/>'s
        /// aspect ratio, so drawing it across the whole framebuffer fills the screen without distorting
        /// the image (cover scaling).
        /// </summary>
        private static Rectangle CoverSource(Texture texture, Vector2i screen)
        {
            var textureAspect = (float) texture.Width / texture.Height;
            var screenAspect = (float) screen.X / screen.Y;

            var width = texture.Width;
            var height = texture.Height;
            if (screenAspect > textureAspect)
                height = (int) (texture.Width / screenAspect);
            else
                width = (int) (texture.Height * screenAspect);

            return Rectangle.FromSize((texture.Width - width) / 2, (texture.Height - height) / 2, width, height);
        }
    }
}

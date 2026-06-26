using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace MinecraftClone3.States
{
    /// <summary>
    /// The death overlay: a red wash, a "You Died!" banner, and Respawn / Title buttons. Respawn asks the
    /// server to revive the player; <see cref="StateWorld"/> closes this overlay (and snaps the player to spawn)
    /// once the server confirms the revive, so the buttons only send the request.
    /// </summary>
    internal class GuiDeathScreen : GuiBase
    {
        private const int ButtonWidth = 200;
        private const int ButtonHeight = 40;
        private const int ButtonGap = 12;
        private const int TitleScale = 4;
        private const string Title = "You Died!";

        private readonly GameWindow _window;

        public GuiDeathScreen(GameWindow window, WorldClient world)
        {
            _window = window;
            _window.CursorState = CursorState.Normal;

            var x = ((int) ScaledResolution.GuiResolution.X - ButtonWidth) / 2;
            var y = (int) ScaledResolution.GuiResolution.Y / 2;
            var step = ButtonHeight + ButtonGap;

            Elements.Add(new GuiButton(Rectangle.FromSize(x, y, ButtonWidth, ButtonHeight), "Respawn",
                world.SendRespawn));
            Elements.Add(new GuiButton(Rectangle.FromSize(x, y + step, ButtonWidth, ButtonHeight),
                "Title Screen", () => StateEngine.ReplaceState(new GuiMainMenu(_window))));
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
                new Color4(0.5f, 0f, 0f, 0.5f), false);

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, height / 4, TitleScale, Color4.White);

            base.Render();
        }
    }
}

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
    /// Yes/No confirmation overlay over the screen that opened it (used for destructive actions like
    /// deleting a world). Owns no TextInput subscriptions, so an overlay is safe here.
    /// </summary>
    internal class GuiConfirm : GuiBase
    {
        private const int ButtonWidth = 140;
        private const int ButtonHeight = 40;
        private const int ButtonGap = 20;
        private const int MessageScale = 2;

        private readonly GameWindow _window;
        private readonly string _message;

        public GuiConfirm(GameWindow window, string message, Action onConfirm)
        {
            _window = window;
            _message = message;

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            var totalWidth = 2 * ButtonWidth + ButtonGap;
            var x = (width - totalWidth) / 2;
            var y = height / 2 + 20;

            Elements.Add(new GuiButton(Rectangle.FromSize(x, y, ButtonWidth, ButtonHeight), "Yes",
                () => { onConfirm?.Invoke(); IsDead = true; }));
            Elements.Add(new GuiButton(Rectangle.FromSize(x + ButtonWidth + ButtonGap, y, ButtonWidth, ButtonHeight),
                "No", () => IsDead = true));
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
            var messageX = (width - Font.MeasureWidth(_message, MessageScale)) / 2;
            Font.DrawString(_message, messageX, height / 2 - 40, MessageScale, Color4.White);

            base.Render();
        }
    }
}

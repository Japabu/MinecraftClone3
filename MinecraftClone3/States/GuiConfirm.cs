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
    /// Yes/No confirmation overlay over the screen that opened it (used for destructive actions like
    /// deleting a world). As the top overlay it is the foreground input layer while open.
    /// </summary>
    internal class GuiConfirm : GuiBase
    {
        private const int ButtonWidth = 140;
        private const int ButtonHeight = 40;
        private const int ButtonGap = 20;
        private const int MessageScale = 2;

        private readonly string _message;

        public GuiConfirm(string message, Action onConfirm)
        {
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

        public override void OnKeyDown(Key key)
        {
            if (key == Key.Escape) IsDead = true;
        }

        public override void Render()
        {
            var screen = new Vector2D<int>(ClientResources.Width, ClientResources.Height);
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, new Rectangle(0, 0, screen.X, screen.Y), null,
                new Vector4D<float>(0f, 0f, 0f, 0.7f), false);

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            var messageX = (width - Font.MeasureWidth(_message, MessageScale)) / 2;
            Font.DrawString(_message, messageX, height / 2 - 40, MessageScale, new Vector4D<float>(1f,1f,1f,1f));

            base.Render();
        }
    }
}

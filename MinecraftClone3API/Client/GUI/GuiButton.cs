using System;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3API.Client.GUI
{
    public class GuiButton : GuiElementBase
    {
        private const int TextScale = 2;
        private const int BorderThickness = 2;

        public Rectangle Bounds;
        public string Label;
        public Action OnClick;
        public bool Enabled = true;

        private bool _hovered;

        public GuiButton(Rectangle bounds, string label, Action onClick)
        {
            Bounds = bounds;
            Label = label;
            OnClick = onClick;
        }

        public override void Update(bool focused)
        {
            if (!focused)
            {
                _hovered = false;
                return;
            }

            var mouse = ClientResources.Window.MouseState;
            var position = ScaledResolution.ToGuiCoords(mouse.Position);
            _hovered = position.X >= Bounds.MinX && position.X <= Bounds.MaxX &&
                       position.Y >= Bounds.MinY && position.Y <= Bounds.MaxY;

            if (Enabled && _hovered &&
                mouse.IsButtonDown(MouseButton.Left) && !mouse.WasButtonDown(MouseButton.Left))
                OnClick?.Invoke();
        }

        public override void Render()
        {
            var fill = Enabled ? (_hovered ? 0.7f : 0.5f) : 0.3f;
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, Bounds, null, new Color4(fill, fill, fill, 1f));
            DrawBorder(Enabled ? 0.8f : 0.4f);

            if (string.IsNullOrEmpty(Label)) return;

            var textX = Bounds.MinX + (Bounds.Width - Font.MeasureWidth(Label, TextScale)) / 2;
            var textY = Bounds.MinY + (Bounds.Height - Font.LineHeight(TextScale)) / 2;
            var textColor = Enabled ? Color4.White : new Color4(0.6f, 0.6f, 0.6f, 1f);
            Font.DrawString(Label, textX, textY, TextScale, textColor);
        }

        private void DrawBorder(float brightness)
        {
            var color = new Color4(brightness, brightness, brightness, 1f);
            GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                new Rectangle(Bounds.MinX, Bounds.MinY, Bounds.MaxX, Bounds.MinY + BorderThickness), null, color);
            GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                new Rectangle(Bounds.MinX, Bounds.MaxY - BorderThickness, Bounds.MaxX, Bounds.MaxY), null, color);
            GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                new Rectangle(Bounds.MinX, Bounds.MinY, Bounds.MinX + BorderThickness, Bounds.MaxY), null, color);
            GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                new Rectangle(Bounds.MaxX - BorderThickness, Bounds.MinY, Bounds.MaxX, Bounds.MaxY), null, color);
        }
    }
}

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

        private static readonly Color4 FillNormal = new Color4(0.5f, 0.5f, 0.5f, 1f);
        private static readonly Color4 FillHovered = new Color4(0.7f, 0.7f, 0.7f, 1f);
        private static readonly Color4 FillDisabled = new Color4(0.3f, 0.3f, 0.3f, 1f);
        private static readonly Color4 BorderEnabled = new Color4(0.8f, 0.8f, 0.8f, 1f);
        private static readonly Color4 BorderDisabled = new Color4(0.4f, 0.4f, 0.4f, 1f);
        private static readonly Color4 TextDisabled = new Color4(0.6f, 0.6f, 0.6f, 1f);

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
            var fill = Enabled ? (_hovered ? FillHovered : FillNormal) : FillDisabled;
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, Bounds, null, fill);
            DrawBorder(Enabled ? BorderEnabled : BorderDisabled);

            if (string.IsNullOrEmpty(Label)) return;

            var textX = Bounds.MinX + (Bounds.Width - Font.MeasureWidth(Label, TextScale)) / 2;
            var textY = Bounds.MinY + (Bounds.Height - Font.LineHeight(TextScale)) / 2;
            var textColor = Enabled ? Color4.White : TextDisabled;
            Font.DrawString(Label, textX, textY, TextScale, textColor);
        }

        private void DrawBorder(Color4 color)
        {
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

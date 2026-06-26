using System;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Util;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.GUI
{
    public class GuiButton : GuiElementBase
    {
        private const int TextScale = 2;
        private const int BorderThickness = 2;

        private static readonly Vector4D<float> FillNormal = new Vector4D<float>(0.5f, 0.5f, 0.5f, 1f);
        private static readonly Vector4D<float> FillHovered = new Vector4D<float>(0.7f, 0.7f, 0.7f, 1f);
        private static readonly Vector4D<float> FillDisabled = new Vector4D<float>(0.3f, 0.3f, 0.3f, 1f);
        private static readonly Vector4D<float> BorderEnabled = new Vector4D<float>(0.8f, 0.8f, 0.8f, 1f);
        private static readonly Vector4D<float> BorderDisabled = new Vector4D<float>(0.4f, 0.4f, 0.4f, 1f);
        private static readonly Vector4D<float> TextDisabled = new Vector4D<float>(0.6f, 0.6f, 0.6f, 1f);

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

            var position = ScaledResolution.ToGuiCoords(ClientResources.Input.MousePosition);
            _hovered = Contains(position);
        }

        public override void OnMouseDown(MouseButton button, Vector2D<float> guiPos)
        {
            if (Enabled && button == MouseButton.Left && Contains(guiPos))
                OnClick?.Invoke();
        }

        private bool Contains(Vector2D<float> p) =>
            p.X >= Bounds.MinX && p.X <= Bounds.MaxX && p.Y >= Bounds.MinY && p.Y <= Bounds.MaxY;

        public override void Render()
        {
            var fill = Enabled ? (_hovered ? FillHovered : FillNormal) : FillDisabled;
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, Bounds, null, fill);
            DrawBorder(Enabled ? BorderEnabled : BorderDisabled);

            if (string.IsNullOrEmpty(Label)) return;

            var textX = Bounds.MinX + (Bounds.Width - Font.MeasureWidth(Label, TextScale)) / 2;
            var textY = Bounds.MinY + (Bounds.Height - Font.LineHeight(TextScale)) / 2;
            var textColor = Enabled ? new Vector4D<float>(1f,1f,1f,1f) : TextDisabled;
            Font.DrawString(Label, textX, textY, TextScale, textColor);
        }

        private void DrawBorder(Vector4D<float> color)
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

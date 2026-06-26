using System;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Util;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// A horizontal drag slider for a continuous (or step-quantized) value. Mirrors <see cref="GuiButton"/>'s
    /// conventions: a <see cref="Rectangle"/> in 960x540 GUI space, mouse mapped via
    /// <see cref="ScaledResolution.ToGuiCoords"/>, drawn from <see cref="ClientResources.WhitePixel"/> + the
    /// bitmap font. Click-and-drag inside the track sets the value (snapped to <c>step</c>, clamped to
    /// [min,max]); <see cref="OnChange"/> fires only when the snapped value actually changes. The label reads
    /// "<c>Label</c>: <c>formatted value</c>".
    /// </summary>
    public class GuiSlider : GuiElementBase
    {
        private const int TextScale = 2;
        private const int BorderThickness = 2;
        private const int HandleWidth = 10;

        private static readonly Vector4D<float> Track = new Vector4D<float>(0.3f, 0.3f, 0.3f, 1f);
        private static readonly Vector4D<float> Fill = new Vector4D<float>(0.45f, 0.55f, 0.7f, 1f);
        private static readonly Vector4D<float> HandleNormal = new Vector4D<float>(0.7f, 0.7f, 0.7f, 1f);
        private static readonly Vector4D<float> HandleActive = new Vector4D<float>(0.9f, 0.9f, 0.9f, 1f);
        private static readonly Vector4D<float> Border = new Vector4D<float>(0.8f, 0.8f, 0.8f, 1f);

        public Rectangle Bounds;
        public string Label;
        public Action<float> OnChange;
        public bool Enabled = true;

        private readonly float _min;
        private readonly float _max;
        private readonly float _step;
        private readonly Func<float, string> _format;
        private float _value;
        private bool _dragging;

        public GuiSlider(Rectangle bounds, string label, float min, float max, float step, float value,
            Action<float> onChange, Func<float, string> format = null)
        {
            Bounds = bounds;
            Label = label;
            _min = min;
            _max = max;
            _step = step;
            _value = Snap(value);
            OnChange = onChange;
            _format = format ?? (v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        public override void Update(bool focused)
        {
            // While the button is held the drag follows the live cursor, even past the track edges, so a fast
            // drag doesn't drop the grab. Position is read level (the press/release are the OnMouse* edges).
            if (!focused || !Enabled) { _dragging = false; return; }
            if (_dragging) ApplyFromPosition(ScaledResolution.ToGuiCoords(ClientResources.Input.MousePosition));
        }

        public override void OnMouseDown(MouseButton button, Vector2D<float> guiPos)
        {
            if (!Enabled || button != MouseButton.Left) return;
            var inside = guiPos.X >= Bounds.MinX && guiPos.X <= Bounds.MaxX &&
                         guiPos.Y >= Bounds.MinY && guiPos.Y <= Bounds.MaxY;
            if (!inside) return;
            _dragging = true;
            ApplyFromPosition(guiPos);
        }

        public override void OnMouseUp(MouseButton button, Vector2D<float> guiPos)
        {
            if (button == MouseButton.Left) _dragging = false;
        }

        private void ApplyFromPosition(Vector2D<float> position)
        {
            if (Bounds.Width <= HandleWidth || _max <= _min) return;
            // Map over the same effective range Render draws the handle across (Width - HandleWidth) and offset
            // by half the handle, so the handle centre sits under the cursor instead of trailing it.
            var t = Math.Clamp((position.X - Bounds.MinX - HandleWidth / 2f) / (Bounds.Width - HandleWidth), 0f, 1f);
            var snapped = Snap(_min + t * (_max - _min));
            if (snapped != _value)
            {
                _value = snapped;
                OnChange?.Invoke(_value);
            }
        }

        public override void Render()
        {
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, Bounds, null, Track);

            var t = (_max - _min) <= 0 ? 0f : Math.Clamp((_value - _min) / (_max - _min), 0f, 1f);
            var handleX = Bounds.MinX + (int) (t * (Bounds.Width - HandleWidth));

            if (handleX > Bounds.MinX)
                GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                    new Rectangle(Bounds.MinX, Bounds.MinY, handleX, Bounds.MaxY), null, Fill);

            GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                new Rectangle(handleX, Bounds.MinY, handleX + HandleWidth, Bounds.MaxY), null,
                _dragging ? HandleActive : HandleNormal);

            DrawBorder(Border);

            var text = Label + ": " + _format(_value);
            var textX = Bounds.MinX + (Bounds.Width - Font.MeasureWidth(text, TextScale)) / 2;
            var textY = Bounds.MinY + (Bounds.Height - Font.LineHeight(TextScale)) / 2;
            Font.DrawString(text, textX, textY, TextScale, new Vector4D<float>(1f,1f,1f,1f));
        }

        private float Snap(float value)
        {
            value = Math.Clamp(value, _min, _max);
            if (_step <= 0) return value;
            var steps = (float) Math.Round((value - _min) / _step);
            return Math.Clamp(_min + steps * _step, _min, _max);
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

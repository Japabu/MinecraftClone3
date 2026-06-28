using System;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Util;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// A single-line editable text field. Characters arrive through <see cref="OnCharTyped"/> (Silk's
    /// <c>KeyChar</c> event, routed by the owning <see cref="GuiBase"/>), so keyboard layout and shift are
    /// handled by the OS. A left click sets focus to whether the click landed inside, so clicking another
    /// field defocuses this one with no coordination.
    /// </summary>
    public class GuiTextInput : GuiElementBase
    {
        private const int TextScale = 2;
        private const int BorderThickness = 2;
        private const int TextPadding = 6;

        private static readonly Vector4D<float> Fill = new Vector4D<float>(0.15f, 0.15f, 0.15f, 1f);
        private static readonly Vector4D<float> BorderNormal = new Vector4D<float>(0.6f, 0.6f, 0.6f, 1f);
        private static readonly Vector4D<float> BorderFocused = new Vector4D<float>(1f, 1f, 1f, 1f);
        private static readonly Vector4D<float> PlaceholderColor = new Vector4D<float>(0.5f, 0.5f, 0.5f, 1f);

        public Rectangle Bounds;
        public string Value;
        public bool Focused;

        private readonly string _placeholder;
        private readonly int _maxLength;

        public GuiTextInput(Rectangle bounds, string value = "", string placeholder = "", int maxLength = 32)
        {
            Bounds = bounds;
            Value = value;
            _placeholder = placeholder;
            _maxLength = maxLength;
        }

        public override void Update(bool focused) { }

        public override void OnMouseDown(MouseButton button, Vector2D<float> guiPos)
        {
            if (button != MouseButton.Left) return;
            Focused = guiPos.X >= Bounds.MinX && guiPos.X <= Bounds.MaxX &&
                      guiPos.Y >= Bounds.MinY && guiPos.Y <= Bounds.MaxY;
        }

        public override void OnCharTyped(char c)
        {
            if (!Focused || char.IsControl(c) || Value.Length >= _maxLength) return;
            Value += c;
        }

        public override void OnKeyDown(Key key)
        {
            if (Focused && key == Key.Backspace && Value.Length > 0)
                Value = Value.Substring(0, Value.Length - 1);
        }

        public override void Render()
        {
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, Bounds, null, Fill);
            DrawBorder(Focused ? BorderFocused : BorderNormal);

            var textX = Bounds.MinX + TextPadding;
            var textY = Bounds.MinY + (Bounds.Height - Font.LineHeight(TextScale)) / 2;

            if (Value.Length == 0 && !Focused)
            {
                if (!string.IsNullOrEmpty(_placeholder))
                    Font.DrawString(_placeholder, textX, textY, TextScale, PlaceholderColor);
                return;
            }

            Font.DrawString(Value, textX, textY, TextScale, new Vector4D<float>(1f,1f,1f,1f));

            if (Focused && CaretVisible())
            {
                var caretX = textX + Font.MeasureWidth(Value, TextScale);
                Font.DrawString("|", caretX, textY, TextScale, new Vector4D<float>(1f,1f,1f,1f));
            }
        }

        private static bool CaretVisible() => Environment.TickCount64 / 500 % 2 == 0;

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

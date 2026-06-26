using System;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// A single-line editable text field. Characters arrive through the window's <c>TextInput</c> event
    /// (so keyboard layout and shift are handled by the OS, unlike polling key states); the owning state
    /// must call <see cref="Detach"/> from its <c>Exit</c> to unsubscribe. A left click anywhere sets focus
    /// to whether the click landed inside, so clicking another field defocuses this one with no coordination.
    /// </summary>
    public class GuiTextInput : GuiElementBase
    {
        private const int TextScale = 2;
        private const int BorderThickness = 2;
        private const int TextPadding = 6;

        private static readonly Color4 Fill = new Color4(0.15f, 0.15f, 0.15f, 1f);
        private static readonly Color4 BorderNormal = new Color4(0.6f, 0.6f, 0.6f, 1f);
        private static readonly Color4 BorderFocused = new Color4(1f, 1f, 1f, 1f);
        private static readonly Color4 PlaceholderColor = new Color4(0.5f, 0.5f, 0.5f, 1f);

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

            ClientResources.Window.TextInput += OnTextInput;
        }

        /// <summary>Unsubscribes from the window's TextInput event. Must be called when the owning state
        /// exits, or the handler leaks and keeps mutating a dead field.</summary>
        public void Detach()
        {
            ClientResources.Window.TextInput -= OnTextInput;
        }

        private void OnTextInput(TextInputEventArgs e)
        {
            if (!Focused) return;

            foreach (var c in e.AsString)
            {
                if (char.IsControl(c)) continue;
                if (Value.Length >= _maxLength) break;
                Value += c;
            }
        }

        public override void Update(bool focused)
        {
            if (!focused) return;

            var mouse = ClientResources.Window.MouseState;
            if (mouse.IsButtonDown(MouseButton.Left) && !mouse.WasButtonDown(MouseButton.Left))
            {
                var position = ScaledResolution.ToGuiCoords(mouse.Position);
                Focused = position.X >= Bounds.MinX && position.X <= Bounds.MaxX &&
                          position.Y >= Bounds.MinY && position.Y <= Bounds.MaxY;
            }

            if (Focused && Value.Length > 0 &&
                ClientResources.Window.KeyboardState.IsKeyPressed(Keys.Backspace))
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

            Font.DrawString(Value, textX, textY, TextScale, Color4.White);

            if (Focused && CaretVisible())
            {
                var caretX = textX + Font.MeasureWidth(Value, TextScale);
                Font.DrawString("|", caretX, textY, TextScale, Color4.White);
            }
        }

        private static bool CaretVisible() => Environment.TickCount64 / 500 % 2 == 0;

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

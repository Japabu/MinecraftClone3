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
    /// Controls screen: one row per <see cref="GameAction"/> with a button showing its current key. Clicking a
    /// row arms it; the next key press rebinds it (Escape cancels the arm). Shown as an overlay over whatever
    /// opened it. Bindings persist immediately via <see cref="Keybinds"/>.
    /// </summary>
    internal class GuiControls : GuiBase
    {
        private const int RowWidth = 320;
        private const int RowHeight = 28;
        private const int RowGap = 5;
        private const int TitleScale = 3;
        private const int TitleY = 24;
        private const string Title = "Controls";

        private readonly GuiButton[] _buttons;
        private int _listening = -1;

        public GuiControls()
        {
            ClientResources.Input.CursorMode = CursorMode.Normal;

            var actions = Keybinds.All;
            _buttons = new GuiButton[actions.Count];

            var x = ((int) ScaledResolution.GuiResolution.X - RowWidth) / 2;
            var y0 = 60;
            var step = RowHeight + RowGap;
            var row = 0;

            for (var i = 0; i < actions.Count; i++)
            {
                var idx = i;
                _buttons[i] = new GuiButton(Row(x, y0, step, row++), RowLabel(idx), () => Arm(idx));
                Elements.Add(_buttons[i]);
            }

            Elements.Add(new GuiButton(Row(x, y0, step, row++), "Reset to Defaults", () =>
            {
                Keybinds.ResetDefaults();
                RefreshLabels();
            }));
            Elements.Add(new GuiButton(Row(x, y0, step, row), "Done", () => IsDead = true));
        }

        private void Arm(int index)
        {
            _listening = index;
            _buttons[index].Label = Keybinds.DisplayName(Keybinds.All[index]) + ": > ? <";
        }

        private void RefreshLabels()
        {
            for (var i = 0; i < _buttons.Length && i < Keybinds.All.Count; i++)
                _buttons[i].Label = RowLabel(i);
        }

        private static string RowLabel(int index)
        {
            var action = Keybinds.All[index];
            return Keybinds.DisplayName(action) + ": " + Keybinds.Get(action);
        }

        public override void OnKeyDown(Key key)
        {
            if (_listening >= 0)
            {
                if (key == Key.Escape)
                {
                    _buttons[_listening].Label = RowLabel(_listening);
                    _listening = -1;
                    return;
                }

                if (key == Key.Unknown) return;
                Keybinds.Set(Keybinds.All[_listening], key);
                _buttons[_listening].Label = RowLabel(_listening);
                _listening = -1;
                return;
            }

            if (key == Key.Escape) IsDead = true;
            else base.OnKeyDown(key);
        }

        public override void OnMouseDown(MouseButton button, Vector2D<float> guiPos)
        {
            if (_listening >= 0) return; // swallow clicks while waiting for a key
            base.OnMouseDown(button, guiPos);
        }

        public override void Render()
        {
            var screen = new Vector2D<int>(ClientResources.Width, ClientResources.Height);
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, new Rectangle(0, 0, screen.X, screen.Y), null,
                new Vector4D<float>(0f, 0f, 0f, 0.7f), false);

            var width = (int) ScaledResolution.GuiResolution.X;
            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, TitleY, TitleScale, new Vector4D<float>(1f,1f,1f,1f));

            base.Render();
        }

        private static Rectangle Row(int x, int y0, int step, int row)
            => Rectangle.FromSize(x, y0 + row * step, RowWidth, RowHeight);
    }
}

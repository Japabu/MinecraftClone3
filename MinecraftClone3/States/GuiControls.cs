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

        private readonly GameWindow _window;
        private readonly GuiButton[] _buttons;
        private int _listening = -1;

        public GuiControls(GameWindow window)
        {
            _window = window;
            _window.CursorState = CursorState.Normal;

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

        public override void Update(bool focused)
        {
            if (!focused) return;

            var ks = _window.KeyboardState;

            if (_listening >= 0)
            {
                if (ks.IsKeyPressed(Keys.Escape))
                {
                    _buttons[_listening].Label = RowLabel(_listening);
                    _listening = -1;
                    return;
                }

                foreach (Keys key in Enum.GetValues(typeof(Keys)))
                {
                    if (key == Keys.Unknown || !ks.IsKeyPressed(key)) continue;
                    Keybinds.Set(Keybinds.All[_listening], key);
                    _buttons[_listening].Label = RowLabel(_listening);
                    _listening = -1;
                    return;
                }

                return; // swallow clicks while waiting for a key
            }

            base.Update(focused);
            if (ks.IsKeyPressed(Keys.Escape)) IsDead = true;
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
            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, TitleY, TitleScale, Color4.White);

            base.Render();
        }

        private static Rectangle Row(int x, int y0, int step, int row)
            => Rectangle.FromSize(x, y0 + row * step, RowWidth, RowHeight);
    }
}

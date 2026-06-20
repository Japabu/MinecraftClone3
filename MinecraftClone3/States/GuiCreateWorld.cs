using System;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using MinecraftClone3API.WorldGen;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3.States
{
    /// <summary>
    /// New-world form: a name and an optional seed. A blank seed is random; a seed that parses as a number
    /// is used directly, otherwise it is hashed to one (stably, so the same text reproduces the same world).
    /// A state (not an overlay) so its <see cref="Exit"/> reliably detaches the text inputs' TextInput hooks.
    /// </summary>
    internal class GuiCreateWorld : GuiBase
    {
        private const int LabelScale = 2;
        private const int TitleScale = 3;
        private const int FieldWidth = 400;
        private const int FieldHeight = 38;
        private const int ButtonWidth = 180;
        private const int ButtonHeight = 40;
        private const int ButtonGap = 14;
        private const string Title = "Create New World";

        private static Texture _background;

        private readonly GameWindow _window;
        private readonly GuiTextInput _nameInput;
        private readonly GuiTextInput _seedInput;
        private readonly GuiButton _create;

        private readonly int _fieldX;
        private readonly int _nameLabelY;
        private readonly int _seedLabelY;

        public GuiCreateWorld(GameWindow window)
        {
            _window = window;
            _window.CursorState = CursorState.Normal;

            if (_background == null)
                _background = ResourceReader.ReadTexture("System/Textures/Gui/ResourceLoadingBackground.png");

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            _fieldX = (width - FieldWidth) / 2;

            _nameLabelY = 150;
            _nameInput = new GuiTextInput(Rectangle.FromSize(_fieldX, _nameLabelY + 26, FieldWidth, FieldHeight),
                "New World", "World name", 32) {Focused = true};
            Elements.Add(_nameInput);

            _seedLabelY = _nameLabelY + 26 + FieldHeight + 24;
            _seedInput = new GuiTextInput(Rectangle.FromSize(_fieldX, _seedLabelY + 26, FieldWidth, FieldHeight),
                "", "leave blank for random", 32);
            Elements.Add(_seedInput);

            var totalWidth = 2 * ButtonWidth + ButtonGap;
            var x = (width - totalWidth) / 2;
            var y = _seedLabelY + 26 + FieldHeight + 36;

            _create = new GuiButton(Rectangle.FromSize(x, y, ButtonWidth, ButtonHeight), "Create New World", Create);
            Elements.Add(_create);

            Elements.Add(new GuiButton(Rectangle.FromSize(x + ButtonWidth + ButtonGap, y, ButtonWidth, ButtonHeight),
                "Cancel", () => StateEngine.ReplaceState(new GuiWorldSelection(_window))));
        }

        private void Create()
        {
            var name = _nameInput.Value.Trim();
            if (name.Length == 0) return;

            var info = WorldManager.CreateWorld(name, ResolveSeed(_seedInput.Value.Trim()));
            StateEngine.ReplaceState(new StateWorld(_window, info));
        }

        private static long ResolveSeed(string text)
        {
            if (text.Length == 0) return Random.Shared.NextInt64();
            if (long.TryParse(text, out var numeric)) return numeric;
            return WorldGenRandom.StableHash(text);
        }

        public override void Update(bool focused)
        {
            base.Update(focused);

            _create.Enabled = _nameInput.Value.Trim().Length > 0;

            if (focused && _window.KeyboardState.IsKeyPressed(Keys.Escape))
                StateEngine.ReplaceState(new GuiWorldSelection(_window));
        }

        public override void Render()
        {
            RenderState.Set(new GlState
            {
                Blend = true,
                BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            });

            var width = (int) ScaledResolution.GuiResolution.X;
            var height = (int) ScaledResolution.GuiResolution.Y;
            GuiRenderer.DrawTexture(_background, new Rectangle(0, 0, width, height), null);

            var titleX = (width - Font.MeasureWidth(Title, TitleScale)) / 2;
            Font.DrawString(Title, titleX, 60, TitleScale, Color4.White);

            Font.DrawString("Name", _fieldX, _nameLabelY, LabelScale, Color4.White);
            Font.DrawString("Seed", _fieldX, _seedLabelY, LabelScale, Color4.White);

            base.Render();
        }

        public override void Exit()
        {
            _nameInput.Detach();
            _seedInput.Detach();
        }
    }
}

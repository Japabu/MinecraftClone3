using System;
using System.Collections.Generic;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3.States
{
    /// <summary>
    /// Scrolling list of saved worlds. Click a row to select it, double-click (or call from a Play button)
    /// to activate it. Scrolls by whole rows so no row is ever partially clipped.
    /// </summary>
    internal class GuiWorldList : GuiElementBase
    {
        private const int RowHeight = 52;
        private const int NameScale = 2;
        private const int SubScale = 1;
        private const long DoubleClickMs = 400;

        private static readonly Color4 RowNormal = new Color4(0.2f, 0.2f, 0.2f, 0.6f);
        private static readonly Color4 RowHovered = new Color4(0.35f, 0.35f, 0.35f, 0.8f);
        private static readonly Color4 RowSelected = new Color4(0.25f, 0.45f, 0.7f, 0.9f);
        private static readonly Color4 SubColor = new Color4(0.7f, 0.7f, 0.7f, 1f);
        private static readonly Color4 EmptyColor = new Color4(0.7f, 0.7f, 0.7f, 1f);

        private readonly Rectangle _bounds;
        private readonly List<WorldInfo> _worlds;
        private readonly Action<WorldInfo> _onActivate;
        private readonly int _visibleRows;

        private int _scroll;
        private int _selectedIndex = -1;
        private int _hoveredIndex = -1;

        private long _lastClickMs;
        private int _lastClickIndex = -1;

        public GuiWorldList(Rectangle bounds, List<WorldInfo> worlds, Action<WorldInfo> onActivate)
        {
            _bounds = bounds;
            _worlds = worlds;
            _onActivate = onActivate;
            _visibleRows = Math.Max(1, bounds.Height / RowHeight);
        }

        public WorldInfo Selected =>
            _selectedIndex >= 0 && _selectedIndex < _worlds.Count ? _worlds[_selectedIndex] : null;

        public override void Update(bool focused)
        {
            _hoveredIndex = -1;
            if (!focused) return;

            var mouse = ClientResources.Window.MouseState;
            var position = ScaledResolution.ToGuiCoords(mouse.Position);

            var maxScroll = Math.Max(0, _worlds.Count - _visibleRows);
            _scroll = MathHelper.Clamp(_scroll - (int) mouse.ScrollDelta.Y, 0, maxScroll);

            var inside = position.X >= _bounds.MinX && position.X <= _bounds.MaxX &&
                         position.Y >= _bounds.MinY && position.Y <= _bounds.MaxY;
            if (inside)
            {
                var row = (int) (position.Y - _bounds.MinY) / RowHeight;
                var index = _scroll + row;
                if (row < _visibleRows && index < _worlds.Count) _hoveredIndex = index;
            }

            if (_hoveredIndex >= 0 &&
                mouse.IsButtonDown(MouseButton.Left) && !mouse.WasButtonDown(MouseButton.Left))
            {
                _selectedIndex = _hoveredIndex;

                var now = Environment.TickCount64;
                if (_lastClickIndex == _hoveredIndex && now - _lastClickMs <= DoubleClickMs)
                    _onActivate?.Invoke(_worlds[_hoveredIndex]);
                _lastClickMs = now;
                _lastClickIndex = _hoveredIndex;
            }
        }

        public override void Render()
        {
            if (_worlds.Count == 0)
            {
                const string empty = "No worlds yet — create one.";
                var x = _bounds.MinX + (_bounds.Width - Font.MeasureWidth(empty, NameScale)) / 2;
                var y = _bounds.MinY + (_bounds.Height - Font.LineHeight(NameScale)) / 2;
                Font.DrawString(empty, x, y, NameScale, EmptyColor);
                return;
            }

            for (var i = 0; i < _visibleRows; i++)
            {
                var index = _scroll + i;
                if (index >= _worlds.Count) break;

                var world = _worlds[index];
                var rowY = _bounds.MinY + i * RowHeight;
                var rowRect = new Rectangle(_bounds.MinX, rowY, _bounds.MaxX, rowY + RowHeight - 4);

                var fill = index == _selectedIndex ? RowSelected : index == _hoveredIndex ? RowHovered : RowNormal;
                GuiRenderer.DrawTexture(ClientResources.WhitePixel, rowRect, null, fill);

                var textX = rowRect.MinX + 8;
                Font.DrawString(world.Name, textX, rowRect.MinY + 6, NameScale, Color4.White);
                var sub = $"Seed: {world.Seed}    {world.LastPlayed:yyyy-MM-dd HH:mm}";
                Font.DrawString(sub, textX, rowRect.MinY + 6 + Font.LineHeight(NameScale) + 2, SubScale, SubColor);
            }
        }
    }
}

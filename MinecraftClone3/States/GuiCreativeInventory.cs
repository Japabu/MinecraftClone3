using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3.States
{
    /// <summary>
    /// Minecraft-style creative inventory: a scrollable grid of every registered block (infinite supply)
    /// over the official <c>creative_inventory/tab_items.png</c> background, with a cursor-held stack and a
    /// bottom hotbar row the player fills by clicking. Hotbar edits are sent to the server (authoritative).
    /// </summary>
    internal class GuiCreativeInventory : GuiBase
    {
        // tab_items.png pixel layout (the used GUI region is 195x136).
        private const int BgWidth = 195;
        private const int BgHeight = 136;
        private const int GridX = 9;
        private const int GridY = 18;
        private const int SlotStride = 18;
        private const int IconSize = 16;
        private const int Columns = 9;
        private const int Rows = 5;
        private const int HotbarY = 112;
        private const int ScrollX = 175;
        private const int ScrollY = 18;
        private const int KnobW = 12;
        private const int KnobH = 15;
        private const int ScrollTrackTexX = 232;   // 12x15 scroll knob sprite origin in widgets.png

        private const int Scale = 2;

        private readonly GameWindow _window;
        private readonly WorldClient _world;
        private readonly List<ushort> _blocks = new List<ushort>();

        private readonly int _bgX;
        private readonly int _bgY;

        private int _scrollRow;
        private ItemStack _cursor = ItemStack.Empty;
        private bool _wasMouseDown;

        public GuiCreativeInventory(GameWindow window, WorldClient world)
        {
            _window = window;
            _world = world;
            _window.CursorState = CursorState.Normal;

            foreach (var block in GameRegistry.Blocks)
                if (block.Id != 0) _blocks.Add(block.Id);

            _bgX = ((int) ScaledResolution.GuiResolution.X - BgWidth * Scale) / 2;
            _bgY = ((int) ScaledResolution.GuiResolution.Y - BgHeight * Scale) / 2;
        }

        private int MaxScrollRow
        {
            get
            {
                var totalRows = (_blocks.Count + Columns - 1) / Columns;
                return totalRows > Rows ? totalRows - Rows : 0;
            }
        }

        public override void Update(bool focused)
        {
            base.Update(focused);
            if (!focused) return;

            var ks = _window.KeyboardState;
            if (ks.IsKeyPressed(Keys.Escape) || ks.IsKeyPressed(Keys.E))
            {
                Close();
                return;
            }

            var scroll = _window.MouseState.ScrollDelta.Y;
            if (scroll > 0) _scrollRow = System.Math.Max(0, _scrollRow - 1);
            else if (scroll < 0) _scrollRow = System.Math.Min(MaxScrollRow, _scrollRow + 1);

            var mouse = _window.MouseState;
            var down = mouse.IsButtonDown(MouseButton.Left);
            if (down && !_wasMouseDown) HandleClick(ScaledResolution.ToGuiCoords(mouse.Position));
            _wasMouseDown = down;
        }

        private void HandleClick(Vector2 pos)
        {
            // Grid: pick up a full stack of that block (creative = infinite). Empty grid cell with a held
            // stack discards it (the item-list acts as a trash slot).
            for (var row = 0; row < Rows; row++)
                for (var col = 0; col < Columns; col++)
                {
                    if (!InSlot(pos, GridX + col * SlotStride, GridY + row * SlotStride)) continue;
                    var index = (_scrollRow + row) * Columns + col;
                    _cursor = index < _blocks.Count
                        ? new ItemStack(_blocks[index], ItemStack.MaxStackSize)
                        : ItemStack.Empty;
                    return;
                }

            // Bottom hotbar row: swap the cursor with the slot, mirrored to the server.
            for (var col = 0; col < Columns; col++)
            {
                if (!InSlot(pos, GridX + col * SlotStride, HotbarY)) continue;
                var slot = _world.Inventory.Slots[col];
                _world.Inventory.Slots[col] = _cursor;
                _cursor = slot;
                _world.SendInventoryAction(col, _world.Inventory.Slots[col]);
                return;
            }
        }

        private bool InSlot(Vector2 pos, int texX, int texY)
        {
            var x = _bgX + texX * Scale;
            var y = _bgY + texY * Scale;
            var size = IconSize * Scale;
            return pos.X >= x && pos.X < x + size && pos.Y >= y && pos.Y < y + size;
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
                new Color4(0f, 0f, 0f, 0.4f), false);

            var background = GuiAssets.Get(GuiAssets.CreativeTab);
            if (background != null)
                GuiRenderer.DrawTexture(background,
                    Rectangle.FromSize(_bgX, _bgY, BgWidth * Scale, BgHeight * Scale),
                    new Rectangle(0, 0, BgWidth, BgHeight));
            else
                GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                    Rectangle.FromSize(_bgX, _bgY, BgWidth * Scale, BgHeight * Scale), null,
                    new Color4(0.15f, 0.15f, 0.15f, 0.95f));

            DrawScrollKnob();

            for (var row = 0; row < Rows; row++)
                for (var col = 0; col < Columns; col++)
                {
                    var index = (_scrollRow + row) * Columns + col;
                    if (index >= _blocks.Count) continue;
                    ItemStackRenderer.Draw(new ItemStack(_blocks[index], 1), SlotRect(GridX + col * SlotStride, GridY + row * SlotStride));
                }

            for (var col = 0; col < Columns; col++)
                ItemStackRenderer.Draw(_world.Inventory.Slots[col], SlotRect(GridX + col * SlotStride, HotbarY));

            if (!_cursor.IsEmpty)
            {
                var pos = ScaledResolution.ToGuiCoords(_window.MouseState.Position);
                ItemStackRenderer.Draw(_cursor,
                    Rectangle.FromSize((int) pos.X - IconSize * Scale / 2, (int) pos.Y - IconSize * Scale / 2,
                        IconSize * Scale, IconSize * Scale));
            }
        }

        private void DrawScrollKnob()
        {
            var widgets = GuiAssets.Get(GuiAssets.Widgets);
            if (widgets == null) return;

            var trackPixels = (Rows - 1) * SlotStride;
            var frac = MaxScrollRow == 0 ? 0f : (float) _scrollRow / MaxScrollRow;
            var knobX = _bgX + ScrollX * Scale;
            var knobY = _bgY + (int) ((ScrollY + frac * trackPixels) * Scale);
            GuiRenderer.DrawTexture(widgets, Rectangle.FromSize(knobX, knobY, KnobW * Scale, KnobH * Scale),
                new Rectangle(ScrollTrackTexX, 0, ScrollTrackTexX + KnobW, KnobH));
        }

        private Rectangle SlotRect(int texX, int texY)
            => Rectangle.FromSize(_bgX + texX * Scale, _bgY + texY * Scale, IconSize * Scale, IconSize * Scale);

        private void Close()
        {
            _window.CursorState = CursorState.Grabbed;
            PlayerController.ResetMouse();
            IsDead = true;
        }
    }
}

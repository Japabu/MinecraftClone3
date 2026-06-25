using MinecraftClone3API.Client.Blocks;
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

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// The 3×3 crafting-table screen (opened by right-clicking a crafting table), over the official
    /// <c>container/crafting_table.png</c>. Shows the 3×3 grid, the result slot, and the full player inventory
    /// so items can be moved in and results taken out; standard pick/place/swap with the cursor-held stack and
    /// hover tooltips. The grid is scratch — its contents return to the inventory when the screen closes.
    /// </summary>
    public class GuiCraftingTable : GuiBase
    {
        // crafting_table.png pixel layout (the container region is 176x166).
        private const int GuiW = 176;
        private const int GuiH = 166;
        private const int GridX = 30;
        private const int GridY = 17;
        private const int ResultX = 124;
        private const int ResultY = 35;
        private const int InvX = 8;
        private const int InvY = 84;
        private const int HotbarY = 142;
        private const int SlotStride = 18;
        private const int IconSize = 16;
        private const int Scale = 2;

        private readonly GameWindow _window;
        private readonly WorldClient _world;
        private readonly CraftingState _crafting;

        private readonly int _bgX;
        private readonly int _bgY;

        private ItemStack _cursor = ItemStack.Empty;
        private bool _wasMouseDown;

        public GuiCraftingTable(GameWindow window, WorldClient world)
        {
            _window = window;
            _world = world;
            _crafting = new CraftingState(world, 3);
            _window.CursorState = CursorState.Normal;

            _bgX = ((int) ScaledResolution.GuiResolution.X - GuiW * Scale) / 2;
            _bgY = ((int) ScaledResolution.GuiResolution.Y - GuiH * Scale) / 2;
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

            var mouse = _window.MouseState;
            var down = mouse.IsButtonDown(MouseButton.Left);
            if (down && !_wasMouseDown) HandleClick(ScaledResolution.ToGuiCoords(mouse.Position));
            _wasMouseDown = down;
        }

        private void HandleClick(Vector2 pos)
        {
            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 3; col++)
                    if (InSlot(pos, GridX + col * SlotStride, GridY + row * SlotStride))
                    {
                        _crafting.InteractGrid(row * 3 + col, ref _cursor);
                        return;
                    }

            if (InSlot(pos, ResultX, ResultY))
            {
                _crafting.TakeResult(ref _cursor);
                return;
            }

            // Main inventory (indices 9..35) then hotbar (0..8).
            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 9; col++)
                    if (InSlot(pos, InvX + col * SlotStride, InvY + row * SlotStride))
                    {
                        _crafting.InteractInventory(9 + row * 9 + col, ref _cursor);
                        return;
                    }

            for (var col = 0; col < 9; col++)
                if (InSlot(pos, InvX + col * SlotStride, HotbarY))
                {
                    _crafting.InteractInventory(col, ref _cursor);
                    return;
                }
        }

        public override void Render()
        {
            SetBlend();

            var screen = _window.FramebufferSize;
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, new Rectangle(0, 0, screen.X, screen.Y), null,
                new Color4(0f, 0f, 0f, 0.4f), false);

            var bg = GuiAssets.Get(GuiAssets.CraftingTable);
            if (bg != null)
                GuiRenderer.DrawTexture(bg, Rectangle.FromSize(_bgX, _bgY, GuiW * Scale, GuiH * Scale),
                    new Rectangle(0, 0, GuiW, GuiH));
            else
                DrawPlaceholder();

            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 3; col++)
                    ItemStackRenderer.Draw(_crafting.Grid[row * 3 + col], SlotRect(GridX + col * SlotStride, GridY + row * SlotStride));

            ItemStackRenderer.Draw(_crafting.Result, SlotRect(ResultX, ResultY));

            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 9; col++)
                    ItemStackRenderer.Draw(_world.Inventory.Slots[9 + row * 9 + col], SlotRect(InvX + col * SlotStride, InvY + row * SlotStride));

            for (var col = 0; col < 9; col++)
                ItemStackRenderer.Draw(_world.Inventory.Slots[col], SlotRect(InvX + col * SlotStride, HotbarY));

            var mousePos = ScaledResolution.ToGuiCoords(_window.MouseState.Position);
            if (!_cursor.IsEmpty)
                ItemStackRenderer.Draw(_cursor, Rectangle.FromSize((int) mousePos.X - IconSize * Scale / 2,
                    (int) mousePos.Y - IconSize * Scale / 2, IconSize * Scale, IconSize * Scale));
            else
                GuiTooltip.Draw(HoveredStack(mousePos), mousePos);
        }

        private void DrawPlaceholder()
        {
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, Rectangle.FromSize(_bgX, _bgY, GuiW * Scale, GuiH * Scale),
                null, new Color4(0.16f, 0.16f, 0.16f, 0.96f));

            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 3; col++)
                    DrawSlotFrame(GridX + col * SlotStride, GridY + row * SlotStride);
            DrawSlotFrame(ResultX, ResultY);
            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 9; col++)
                    DrawSlotFrame(InvX + col * SlotStride, InvY + row * SlotStride);
            for (var col = 0; col < 9; col++)
                DrawSlotFrame(InvX + col * SlotStride, HotbarY);
        }

        private void DrawSlotFrame(int texX, int texY)
            => GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                Rectangle.FromSize(_bgX + texX * Scale, _bgY + texY * Scale, IconSize * Scale, IconSize * Scale), null,
                new Color4(0.36f, 0.36f, 0.36f, 1f));

        private ItemStack HoveredStack(Vector2 pos)
        {
            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 3; col++)
                    if (InSlot(pos, GridX + col * SlotStride, GridY + row * SlotStride))
                        return _crafting.Grid[row * 3 + col];

            if (InSlot(pos, ResultX, ResultY)) return _crafting.Result;

            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 9; col++)
                    if (InSlot(pos, InvX + col * SlotStride, InvY + row * SlotStride))
                        return _world.Inventory.Slots[9 + row * 9 + col];

            for (var col = 0; col < 9; col++)
                if (InSlot(pos, InvX + col * SlotStride, HotbarY))
                    return _world.Inventory.Slots[col];

            return ItemStack.Empty;
        }

        private bool InSlot(Vector2 pos, int texX, int texY)
        {
            var x = _bgX + texX * Scale;
            var y = _bgY + texY * Scale;
            var size = IconSize * Scale;
            return pos.X >= x && pos.X < x + size && pos.Y >= y && pos.Y < y + size;
        }

        private Rectangle SlotRect(int texX, int texY)
            => Rectangle.FromSize(_bgX + texX * Scale, _bgY + texY * Scale, IconSize * Scale, IconSize * Scale);

        private static void SetBlend() => RenderState.Set(new GlState
        {
            Blend = true,
            BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
        });

        private void Close()
        {
            _crafting.ReturnAllToInventory(ref _cursor);
            _window.CursorState = CursorState.Grabbed;
            PlayerController.ResetMouse();
            IsDead = true;
        }
    }
}

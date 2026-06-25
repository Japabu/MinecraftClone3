using System.Collections.Generic;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// The 3×3 crafting-table screen (opened by right-clicking a crafting table), over the official
    /// <c>container/crafting_table.png</c>. The 3×3 grid is scratch (returns to the inventory on close); the
    /// result slot crafts from the registered recipes. Full vanilla slot interaction (pick/place/split/drag,
    /// shift-click the result to craft a batch) comes from <see cref="ContainerScreen"/>.
    /// </summary>
    public class GuiCraftingTable : ContainerScreen
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
        private const int SlotSize = 16;
        private const int Scale = 2;

        private const int GridGroup = 1;
        private const int MainGroup = 2;
        private const int HotbarGroup = 3;

        private readonly WorldClient _world;
        private readonly CraftingState _crafting;
        private readonly List<Slot> _slots = new List<Slot>();

        private readonly int _bgX;
        private readonly int _bgY;

        public GuiCraftingTable(GameWindow window, WorldClient world) : base(window)
        {
            _world = world;
            _crafting = new CraftingState(world, 3);

            _bgX = ((int) ScaledResolution.GuiResolution.X - GuiW * Scale) / 2;
            _bgY = ((int) ScaledResolution.GuiResolution.Y - GuiH * Scale) / 2;

            BuildSlots();
        }

        protected override IReadOnlyList<Slot> Slots => _slots;

        private void BuildSlots()
        {
            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 3; col++)
                {
                    var index = row * 3 + col;
                    _slots.Add(new Slot(SlotRect(GridX + col * SlotStride, GridY + row * SlotStride),
                        () => _crafting.Grid[index], v => _crafting.Grid[index] = v));
                }

            _slots.Add(new Slot(SlotRect(ResultX, ResultY), () => _crafting.Result, null)
            {
                IsOutput = true,
                OnTakeOutput = _crafting.ConsumeOne
            });

            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 9; col++)
                    AddInventorySlot(9 + row * 9 + col, InvX + col * SlotStride, InvY + row * SlotStride);

            for (var col = 0; col < 9; col++)
                AddInventorySlot(col, InvX + col * SlotStride, HotbarY);
        }

        private void AddInventorySlot(int index, int texX, int texY)
        {
            _slots.Add(new Slot(SlotRect(texX, texY), () => _world.Inventory.Slots[index], v =>
            {
                _world.Inventory.Slots[index] = v;
                _world.SendInventoryAction(index, v);
            }));
        }

        protected override void OnShiftClick(Slot slot)
        {
            if (!slot.IsOutput) return; // inventory↔grid quick-move not implemented; see docs/inventory.md

            while (!_crafting.Result.IsEmpty)
            {
                _crafting.AddToInventory(_crafting.Result);
                _crafting.ConsumeOne();
            }
        }

        protected override void DrawBackground()
        {
            var bg = GuiAssets.Get(GuiAssets.CraftingTable);
            if (bg != null)
            {
                GuiRenderer.DrawTexture(bg, Rectangle.FromSize(_bgX, _bgY, GuiW * Scale, GuiH * Scale),
                    new Rectangle(0, 0, GuiW, GuiH));
                return;
            }

            GuiRenderer.DrawTexture(ClientResources.WhitePixel, Rectangle.FromSize(_bgX, _bgY, GuiW * Scale, GuiH * Scale),
                null, new Color4(0.16f, 0.16f, 0.16f, 0.96f));
            foreach (var slot in _slots)
                GuiRenderer.DrawTexture(ClientResources.WhitePixel, slot.Bounds, null, new Color4(0.36f, 0.36f, 0.36f, 1f));
        }

        protected override void OnClosed()
        {
            _crafting.ReturnGridToInventory();
            if (!Cursor.IsEmpty)
            {
                _crafting.AddToInventory(Cursor);
                Cursor = ItemStack.Empty;
            }
        }

        private Rectangle SlotRect(int texX, int texY)
            => Rectangle.FromSize(_bgX + texX * Scale, _bgY + texY * Scale, SlotSize * Scale, SlotSize * Scale);
    }
}

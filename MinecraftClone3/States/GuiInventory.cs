using System.Collections.Generic;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;

namespace MinecraftClone3.States
{
    /// <summary>
    /// The survival player inventory (opened with the Inventory key in survival): the 2×2 crafting grid + result,
    /// the 3×9 main inventory, and the hotbar, over the official <c>container/inventory.png</c>. The 2×2 grid is
    /// scratch (returns to the inventory on close). Full vanilla slot interaction (pick/place/split/drag,
    /// shift-click quick-move) comes from <see cref="ContainerScreen"/>. Armor / offhand / the 3D player preview
    /// are not modelled, so those regions of the background are inert.
    /// </summary>
    internal class GuiInventory : ContainerScreen
    {
        // inventory.png pixel layout (the container region is 176x166).
        private const int GuiW = 176;
        private const int GuiH = 166;
        private const int GridX = 98;
        private const int GridY = 18;
        private const int ResultX = 154;
        private const int ResultY = 28;
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

        public GuiInventory(GameWindow window, WorldClient world) : base(window)
        {
            _world = world;
            _crafting = new CraftingState(world, 2);

            _bgX = ((int) ScaledResolution.GuiResolution.X - GuiW * Scale) / 2;
            _bgY = ((int) ScaledResolution.GuiResolution.Y - GuiH * Scale) / 2;

            BuildSlots();
        }

        protected override IReadOnlyList<Slot> Slots => _slots;

        private void BuildSlots()
        {
            for (var row = 0; row < 2; row++)
                for (var col = 0; col < 2; col++)
                {
                    var index = row * 2 + col;
                    _slots.Add(new Slot(SlotRect(GridX + col * SlotStride, GridY + row * SlotStride),
                        () => _crafting.Grid[index], v => _crafting.Grid[index] = v) { Group = GridGroup });
                }

            _slots.Add(new Slot(SlotRect(ResultX, ResultY), () => _crafting.Result, null)
            {
                IsOutput = true,
                OnTakeOutput = _crafting.ConsumeOne
            });

            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 9; col++)
                    AddInventorySlot(9 + row * 9 + col, InvX + col * SlotStride, InvY + row * SlotStride, MainGroup);

            for (var col = 0; col < 9; col++)
                AddInventorySlot(col, InvX + col * SlotStride, HotbarY, HotbarGroup);
        }

        private void AddInventorySlot(int index, int texX, int texY, int group)
        {
            _slots.Add(new Slot(SlotRect(texX, texY), () => _world.Inventory.Slots[index], v =>
            {
                _world.Inventory.Slots[index] = v;
                _world.SendInventoryAction(index, v);
            }) { Group = group });
        }

        protected override void OnShiftClick(Slot slot)
        {
            if (slot.IsOutput)
            {
                while (!_crafting.Result.IsEmpty)
                {
                    _crafting.AddToInventory(_crafting.Result);
                    _crafting.ConsumeOne();
                }
                return;
            }

            // Grid → inventory; within the inventory, main ↔ hotbar (vanilla quick-move).
            IReadOnlyList<Slot> targets;
            if (slot.Group == GridGroup)
            {
                var inv = new List<Slot>(SlotsInGroup(MainGroup));
                inv.AddRange(SlotsInGroup(HotbarGroup));
                targets = inv;
            }
            else
                targets = SlotsInGroup(slot.Group == HotbarGroup ? MainGroup : HotbarGroup);

            slot.Set(MergeInto(slot.Get(), targets));
        }

        protected override void DrawBackground()
        {
            var bg = GuiAssets.Get(GuiAssets.Inventory);
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

using System.Collections.Generic;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// The chest screen (opened by right-clicking a chest), over the official <c>container/generic_54.png</c>.
    /// The 27 storage slots live on the server as the chest's <see cref="ContainerView"/> (streamed each tick);
    /// the screen reads them and mirrors slot edits up with <c>ContainerSlotPacket</c>s, while the player
    /// inventory rows behave exactly as the other container screens. A single chest has no progress fields.
    /// </summary>
    public class GuiChest : ContainerScreen
    {
        // generic_54.png layout: 176 wide, drawn in two blits — the top (title + container rows) from y=0, and
        // the player-inventory portion from y=126 in the texture (the maximal 6-row sheet packs them apart).
        private const int GuiW = 176;
        private const int Rows = 3;
        private const int TopH = 17 + Rows * 18;     // title strip + the chest's slot rows
        private const int InvSrcY = 126;
        private const int InvH = 96;                  // player inventory + hotbar portion
        private const int TotalH = TopH + InvH;

        private const int ContainerY = 18;
        private const int InvY = 85;
        private const int HotbarY = 143;
        private const int InvX = 8;
        private const int SlotStride = 18;
        private const int SlotSize = 16;
        private const int Scale = 2;

        private const int ChestGroup = 1;
        private const int MainGroup = 2;
        private const int HotbarGroup = 3;

        private readonly WorldClient _world;
        private readonly Vector3i _pos;
        private readonly ContainerView _view;
        private readonly List<Slot> _slots = new List<Slot>();

        private readonly int _bgX;
        private readonly int _bgY;

        public GuiChest(GameWindow window, WorldClient world, Vector3i pos) : base(window)
        {
            _world = world;
            _pos = pos;
            _view = world.OpenContainer(pos, BlockDataChestSlot.Count, 0);
            BlockEntityRenderer.SetChestOpen(pos, true);

            _bgX = ((int) ScaledResolution.GuiResolution.X - GuiW * Scale) / 2;
            _bgY = ((int) ScaledResolution.GuiResolution.Y - TotalH * Scale) / 2;

            BuildSlots();
        }

        protected override IReadOnlyList<Slot> Slots => _slots;

        private void BuildSlots()
        {
            for (var row = 0; row < Rows; row++)
                for (var col = 0; col < 9; col++)
                    _slots.Add(ChestSlot(row * 9 + col, InvX + col * SlotStride, ContainerY + row * SlotStride));

            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 9; col++)
                    AddInventorySlot(9 + row * 9 + col, InvX + col * SlotStride, InvY + row * SlotStride, MainGroup);

            for (var col = 0; col < 9; col++)
                AddInventorySlot(col, InvX + col * SlotStride, HotbarY, HotbarGroup);
        }

        private Slot ChestSlot(int index, int texX, int texY)
            => new Slot(SlotRect(texX, texY), () => _view.Slots[index], v => SetChestSlot(index, v)) { Group = ChestGroup };

        private void SetChestSlot(int index, ItemStack stack)
        {
            _view.Slots[index] = stack;
            _world.SendContainerSlot(_pos, index, stack);
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
            if (slot.Group == ChestGroup)
            {
                var inv = new List<Slot>(SlotsInGroup(MainGroup));
                inv.AddRange(SlotsInGroup(HotbarGroup));
                slot.Set(MergeInto(slot.Get(), inv));
            }
            else
            {
                slot.Set(MergeInto(slot.Get(), SlotsInGroup(ChestGroup)));
            }
        }

        protected override void DrawBackground()
        {
            var bg = GuiAssets.Get(GuiAssets.Generic54);
            if (bg != null)
            {
                // Source rects are (minX, minY, maxX, maxY) — NOT x/y/w/h.
                GuiRenderer.DrawTexture(bg, Rectangle.FromSize(_bgX, _bgY, GuiW * Scale, TopH * Scale),
                    new Rectangle(0, 0, GuiW, TopH));
                GuiRenderer.DrawTexture(bg, Rectangle.FromSize(_bgX, _bgY + TopH * Scale, GuiW * Scale, InvH * Scale),
                    new Rectangle(0, InvSrcY, GuiW, InvSrcY + InvH));
            }
            else
            {
                GuiRenderer.DrawTexture(ClientResources.WhitePixel, Rectangle.FromSize(_bgX, _bgY, GuiW * Scale, TotalH * Scale),
                    null, new Color4(0.16f, 0.16f, 0.16f, 0.96f));
                foreach (var s in _slots)
                    GuiRenderer.DrawTexture(ClientResources.WhitePixel, s.Bounds, null, new Color4(0.36f, 0.36f, 0.36f, 1f));
            }
        }

        protected override void OnClosed()
        {
            _world.CloseContainer(_pos);
            BlockEntityRenderer.SetChestOpen(_pos, false);
        }

        private Rectangle SlotRect(int texX, int texY)
            => Rectangle.FromSize(_bgX + texX * Scale, _bgY + texY * Scale, SlotSize * Scale, SlotSize * Scale);
    }

    // Mirrors VanillaPlugin's BlockDataChest slot count. Kept here (not referenced from the plugin) so the API
    // screen need not depend on the content plugin; the count is the container's stable contract.
    internal static class BlockDataChestSlot
    {
        public const int Count = 27;
    }
}

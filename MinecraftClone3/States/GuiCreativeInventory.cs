using System.Collections.Generic;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3.States
{
    /// <summary>
    /// Minecraft-style creative inventory: a scrollable grid of every registered item (infinite supply) over the
    /// official <c>creative_inventory/tab_items.png</c> background, plus the bottom hotbar row the player fills
    /// by clicking. Full vanilla slot interaction (pick/place/split/drag) comes from
    /// <see cref="ContainerScreen"/>; there is no crafting grid here (crafting is the crafting table's job).
    /// Hotbar edits are mirrored to the server (authoritative).
    /// </summary>
    internal class GuiCreativeInventory : ContainerScreen
    {
        // tab_items.png pixel layout (the used GUI region is 195x136).
        private const int BgWidth = 195;
        private const int BgHeight = 136;
        private const int GridX = 9;
        private const int GridY = 18;
        private const int SlotStride = 18;
        private const int SlotSize = 16;
        private const int Columns = 9;
        private const int Rows = 5;
        private const int HotbarY = 112;
        private const int ScrollX = 175;
        private const int ScrollY = 18;
        private const int KnobW = 12;
        private const int KnobH = 15;
        private const int ScrollTrackTexX = 232;   // 12x15 scroll knob sprite origin in widgets.png

        private const int Scale = 2;

        private const int HotbarGroup = 1;

        private readonly WorldClient _world;
        private readonly List<ushort> _items = new List<ushort>();
        private readonly List<Slot> _slots = new List<Slot>();

        private readonly int _bgX;
        private readonly int _bgY;

        private int _scrollRow;

        public GuiCreativeInventory(WorldClient world) : base()
        {
            _world = world;

            foreach (var item in GameRegistry.Items)
                _items.Add(item.Id);

            _bgX = ((int) ScaledResolution.GuiResolution.X - BgWidth * Scale) / 2;
            _bgY = ((int) ScaledResolution.GuiResolution.Y - BgHeight * Scale) / 2;

            BuildSlots();
        }

        protected override IReadOnlyList<Slot> Slots => _slots;

        private void BuildSlots()
        {
            for (var row = 0; row < Rows; row++)
                for (var col = 0; col < Columns; col++)
                {
                    var r = row;
                    var c = col;
                    _slots.Add(new Slot(SlotRect(GridX + c * SlotStride, GridY + r * SlotStride),
                        () =>
                        {
                            var index = (_scrollRow + r) * Columns + c;
                            return index < _items.Count ? new ItemStack(_items[index], 1) : ItemStack.Empty;
                        }, null)
                    {
                        IsSource = true
                    });
                }

            for (var col = 0; col < Columns; col++)
            {
                var index = col;
                _slots.Add(new Slot(SlotRect(GridX + col * SlotStride, HotbarY),
                    () => _world.Inventory.Slots[index], v =>
                    {
                        _world.Inventory.Slots[index] = v;
                        _world.SendInventoryAction(index, v);
                    }) { Group = HotbarGroup });
            }
        }

        protected override void OnShiftClick(Slot slot)
        {
            // Shift-click the item list grants a full stack to the hotbar; shift-click a hotbar slot trashes it
            // (the creative void), matching vanilla creative.
            if (slot.IsSource)
            {
                var give = slot.Get();
                if (give.IsEmpty) return;
                MergeInto(give.WithCount(give.Item?.MaxStackSize ?? ItemStack.MaxStackSize),
                    SlotsInGroup(HotbarGroup));
            }
            else
                slot.Set(ItemStack.Empty);
        }

        private int MaxScrollRow
        {
            get
            {
                var totalRows = (_items.Count + Columns - 1) / Columns;
                return totalRows > Rows ? totalRows - Rows : 0;
            }
        }

        public override void OnScroll(float delta)
        {
            if (delta > 0) _scrollRow = System.Math.Max(0, _scrollRow - 1);
            else if (delta < 0) _scrollRow = System.Math.Min(MaxScrollRow, _scrollRow + 1);
        }

        protected override void DrawBackground()
        {
            var background = GuiAssets.Get(GuiAssets.CreativeTab);
            if (background != null)
                GuiRenderer.DrawTexture(background,
                    Rectangle.FromSize(_bgX, _bgY, BgWidth * Scale, BgHeight * Scale),
                    new Rectangle(0, 0, BgWidth, BgHeight));
            else
                GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                    Rectangle.FromSize(_bgX, _bgY, BgWidth * Scale, BgHeight * Scale), null,
                    new Vector4D<float>(0.15f, 0.15f, 0.15f, 0.95f));

            DrawScrollKnob();
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
            => Rectangle.FromSize(_bgX + texX * Scale, _bgY + texY * Scale, SlotSize * Scale, SlotSize * Scale);
    }
}

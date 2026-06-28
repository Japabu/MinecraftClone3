using System;
using System.Collections.Generic;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace MinecraftClone3.States
{
    /// <summary>
    /// Minecraft's creative search tab: a search box over a scrollable 9×5 grid of every registered item
    /// (infinite supply) on the official <c>creative_inventory/tab_item_search.png</c> background, plus the
    /// bottom hotbar row the player fills by clicking. Typing filters the grid live by item name; the scrollbar
    /// scrolls with the wheel or by dragging its knob, and dims when everything already fits. Full vanilla slot
    /// interaction (pick/place/split/drag) comes from <see cref="ContainerScreen"/>; there is no crafting grid
    /// here (crafting is the crafting table's job). Hotbar edits are mirrored to the server (authoritative).
    /// </summary>
    internal class GuiCreativeInventory : ContainerScreen
    {
        // tab_item_search.png pixel layout (the used GUI region is 195x136), drawn at 2x.
        private const int BgWidth = 195;
        private const int BgHeight = 136;
        private const int GridX = 9;
        private const int GridY = 18;
        private const int SlotStride = 18;
        private const int SlotSize = 16;
        private const int Columns = 9;
        private const int Rows = 5;
        private const int HotbarY = 112;

        // The search box baked into the background: text region (relative px) and a slightly larger hit-test box.
        private const int SearchTextX = 82;
        private const int SearchBoxY = 4;
        private const int SearchBoxH = 12;
        private const int SearchHitX0 = 76;
        private const int SearchHitX1 = 170;
        private const int MaxSearchLength = 50;

        // Scrollbar: a 12x15 knob travelling a 97px groove on the right edge.
        private const int ScrollX = 175;
        private const int ScrollTop = 18;
        private const int ScrollTravel = 97;
        private const int KnobW = 12;
        private const int KnobH = 15;

        private const int Scale = 2;

        private const int HotbarGroup = 1;

        private static readonly Vector4D<float> White = new Vector4D<float>(1f, 1f, 1f, 1f);
        private static readonly Vector4D<float> Faint = new Vector4D<float>(0.5f, 0.5f, 0.5f, 1f);

        private readonly WorldClient _world;
        private readonly List<Entry> _allItems = new List<Entry>();
        private readonly List<ushort> _filtered = new List<ushort>();
        private readonly List<Slot> _slots = new List<Slot>();

        private readonly int _bgX;
        private readonly int _bgY;

        private string _search = "";
        private bool _searchFocused = true; // vanilla auto-focuses the search box on open
        private float _scrollOffs;
        private bool _scrolling;

        // The inventory key that opens this screen also emits a char event the same frame; drop characters until
        // the first Update so that opening with "e" doesn't pre-fill the search box.
        private bool _acceptChars;

        public GuiCreativeInventory(WorldClient world) : base()
        {
            _world = world;

            foreach (var item in GameRegistry.Items)
                _allItems.Add(new Entry(item.Id, SearchKey(item)));

            _bgX = ((int) ScaledResolution.GuiResolution.X - BgWidth * Scale) / 2;
            _bgY = ((int) ScaledResolution.GuiResolution.Y - BgHeight * Scale) / 2;

            BuildSlots();
            ApplyFilter();
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
                            var index = (RowOffset + r) * Columns + c;
                            return index < _filtered.Count ? new ItemStack(_filtered[index], 1) : ItemStack.Empty;
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

        // --- Search ---------------------------------------------------------------------------------------

        private static string SearchKey(Item item)
        {
            // Match the visible name plus the stable ids, so "oak", "minecraft:oak_log" and "oak_log" all hit.
            var name = item.GetName() ?? "";
            var key = item.RegistryKey ?? "";
            var mc = item.MinecraftId ?? "";
            return (name + " " + key + " " + mc).ToLowerInvariant();
        }

        private void ApplyFilter()
        {
            _filtered.Clear();
            if (_search.Length == 0)
                foreach (var e in _allItems) _filtered.Add(e.Id);
            else
            {
                var q = _search.ToLowerInvariant();
                foreach (var e in _allItems)
                    if (e.Search.Contains(q)) _filtered.Add(e.Id);
            }

            _scrollOffs = 0f;
        }

        public override void OnCharTyped(char c)
        {
            if (!_acceptChars || !_searchFocused || char.IsControl(c) || _search.Length >= MaxSearchLength) return;
            _search += c;
            ApplyFilter();
        }

        public override void OnKeyDown(Key key)
        {
            if (key == Key.Escape)
            {
                Close();
                return;
            }

            if (_searchFocused)
            {
                if (key == Key.Backspace && _search.Length > 0)
                {
                    _search = _search.Substring(0, _search.Length - 1);
                    ApplyFilter();
                }
                // Swallow everything else (including the inventory key) so it types instead of closing.
                return;
            }

            if (Keybinds.Matches(GameAction.Inventory, key)) Close();
        }

        // --- Scrolling ------------------------------------------------------------------------------------

        private int TotalRows => (_filtered.Count + Columns - 1) / Columns;
        private int HiddenRows => Math.Max(0, TotalRows - Rows);
        private bool CanScroll => HiddenRows > 0;
        private int RowOffset => (int) (_scrollOffs * HiddenRows + 0.5f);

        public override void OnScroll(float delta)
        {
            if (!CanScroll) return;
            _scrollOffs = Math.Clamp(_scrollOffs - delta / HiddenRows, 0f, 1f);
        }

        public override void OnMouseDown(MouseButton button, Vector2D<float> guiPos)
        {
            if (button == MouseButton.Left)
            {
                _searchFocused = InSearchBox(guiPos);
                if (CanScroll && InScrollColumn(guiPos))
                {
                    _scrolling = true;
                    DragScrollTo(guiPos.Y);
                    return; // consume the press so it doesn't start a slot gesture
                }
            }

            base.OnMouseDown(button, guiPos);
        }

        public override void OnMouseUp(MouseButton button, Vector2D<float> guiPos)
        {
            if (button == MouseButton.Left) _scrolling = false;
            base.OnMouseUp(button, guiPos);
        }

        public override void Update(bool focused)
        {
            base.Update(focused);
            _acceptChars = true;
            if (focused && _scrolling)
                DragScrollTo(ScaledResolution.ToGuiCoords(ClientResources.Input.MousePosition).Y);
        }

        private void DragScrollTo(float guiY)
        {
            var relY = (guiY - _bgY) / Scale;
            _scrollOffs = Math.Clamp((relY - ScrollTop - KnobH / 2f) / ScrollTravel, 0f, 1f);
        }

        private bool InSearchBox(Vector2D<float> p)
            => p.X >= _bgX + SearchHitX0 * Scale && p.X <= _bgX + SearchHitX1 * Scale &&
               p.Y >= _bgY + SearchBoxY * Scale && p.Y <= _bgY + (SearchBoxY + SearchBoxH) * Scale;

        private bool InScrollColumn(Vector2D<float> p)
            => p.X >= _bgX + ScrollX * Scale && p.X <= _bgX + (ScrollX + KnobW) * Scale &&
               p.Y >= _bgY + ScrollTop * Scale && p.Y <= _bgY + (ScrollTop + ScrollTravel + KnobH) * Scale;

        // --- Drawing --------------------------------------------------------------------------------------

        protected override void DrawBackground()
        {
            var background = GuiAssets.Get(GuiAssets.CreativeSearchTab);
            if (background != null)
                GuiRenderer.DrawTexture(background,
                    Rectangle.FromSize(_bgX, _bgY, BgWidth * Scale, BgHeight * Scale),
                    new Rectangle(0, 0, BgWidth, BgHeight));
            else
            {
                GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                    Rectangle.FromSize(_bgX, _bgY, BgWidth * Scale, BgHeight * Scale), null,
                    new Vector4D<float>(0.15f, 0.15f, 0.15f, 0.95f));
                GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                    Rectangle.FromSize(_bgX + SearchHitX0 * Scale, _bgY + SearchBoxY * Scale,
                        (SearchHitX1 - SearchHitX0) * Scale, SearchBoxH * Scale), null,
                    new Vector4D<float>(0f, 0f, 0f, 0.6f));
            }

            DrawSearchText();
            DrawScroller();
        }

        private void DrawSearchText()
        {
            var textX = _bgX + SearchTextX * Scale;
            var textY = _bgY + SearchBoxY * Scale + (SearchBoxH * Scale - Font.LineHeight(Scale)) / 2;

            if (_search.Length == 0 && !_searchFocused)
            {
                Font.DrawString("Search", textX, textY, Scale, Faint);
                return;
            }

            Font.DrawString(_search, textX, textY, Scale, White);
            if (_searchFocused && CaretVisible())
            {
                var caretX = textX + Font.MeasureWidth(_search, Scale);
                Font.DrawString("|", caretX, textY, Scale, White);
            }
        }

        private void DrawScroller()
        {
            var knobY = _bgY + (ScrollTop + (int) (_scrollOffs * ScrollTravel)) * Scale;
            var rect = Rectangle.FromSize(_bgX + ScrollX * Scale, knobY, KnobW * Scale, KnobH * Scale);

            var sprite = GuiAssets.Get(CanScroll ? GuiAssets.Scroller : GuiAssets.ScrollerDisabled);
            if (sprite != null)
                GuiRenderer.DrawTexture(sprite, rect, null);
            else
                GuiRenderer.DrawTexture(ClientResources.WhitePixel, rect, null,
                    CanScroll ? new Vector4D<float>(0.75f, 0.75f, 0.75f, 1f) : new Vector4D<float>(0.4f, 0.4f, 0.4f, 1f));
        }

        private static bool CaretVisible() => Environment.TickCount64 / 500 % 2 == 0;

        private Rectangle SlotRect(int texX, int texY)
            => Rectangle.FromSize(_bgX + texX * Scale, _bgY + texY * Scale, SlotSize * Scale, SlotSize * Scale);

        private readonly struct Entry
        {
            public readonly ushort Id;
            public readonly string Search;

            public Entry(ushort id, string search)
            {
                Id = id;
                Search = search;
            }
        }
    }
}

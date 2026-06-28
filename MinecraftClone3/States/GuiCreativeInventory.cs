using System;
using System.Collections.Generic;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace MinecraftClone3.States
{
    /// <summary>
    /// Minecraft's full creative inventory: a row of category tabs across the top and bottom of the panel, each
    /// showing its bucket of registered items in a scrollable 9×5 grid over <c>tab_items.png</c>; a Search tab
    /// (compass, top-right) with a live filter box over <c>tab_item_search.png</c>; and a Survival-Inventory tab
    /// (chest, bottom-right) with the armor + main inventory + hotbar + destroy slot over <c>tab_inventory.png</c>.
    /// Geometry matches vanilla <c>CreativeModeInventoryScreen</c>. Items declare their tab via
    /// <see cref="MinecraftClone3API.Items.CreativeTab"/>; empty categories are hidden. The bottom hotbar row is
    /// always the player's, mirrored to the server. Full vanilla slot interaction comes from
    /// <see cref="ContainerScreen"/>.
    /// </summary>
    internal class GuiCreativeInventory : ContainerScreen
    {
        // tab_items.png pixel layout (the used GUI region is 195x136), drawn at 2x.
        private const int BgWidth = 195;
        private const int BgHeight = 136;
        private const int GridX = 9;
        private const int GridY = 18;
        private const int SlotStride = 18;
        private const int SlotSize = 16;
        private const int Columns = 9;
        private const int Rows = 5;
        private const int HotbarY = 112;

        // The search box baked into tab_item_search.png: text region + a slightly larger hit-test box.
        private const int SearchTextX = 82;
        private const int SearchBoxY = 4;
        private const int SearchBoxH = 12;
        private const int SearchHitX0 = 76;
        private const int SearchHitX1 = 170;
        private const int MaxSearchLength = 50;

        // Scrollbar: a 12x15 knob. Vanilla renders the knob over a 95px travel but maps drags over 97 (its own
        // off-by-two); matching both keeps the resting position pixel-exact.
        private const int ScrollX = 175;
        private const int ScrollTop = 18;
        private const int ScrollRenderTravel = 95;
        private const float ScrollDragTravel = 97f;
        private const int KnobW = 12;
        private const int KnobH = 15;

        // Tab buttons: 26x32 sprites spaced 27px; the top row sits 28px above the panel, the bottom row 132px
        // below its top (overlapping the panel's lower edge), per vanilla.
        private const int TabW = 26;
        private const int TabH = 32;
        private const int TabSpacing = 27;
        private const int TabTopOffset = 28;
        private const int TabBottomOffset = 132;
        private const int TabHitInsetX = 3;
        private const int TabHitInsetY = 3;
        private const int TabHitW = 21;
        private const int TabHitH = 27;
        private const int TabIconX = 5;
        private const int TabIconY = 8;

        // Survival-Inventory tab slot layout (relative px), matching tab_inventory.png.
        private const int InvArmorLeftX = 54;
        private const int InvArmorRightX = 108;
        private const int InvArmorTopY = 6;
        private const int InvArmorBottomY = 33;
        private const int InvMainY = 54;
        private const int InvDestroyX = 173;
        private const int InvDestroyY = 112;
        private const int InvPlayerX = 73;
        private const int InvPlayerY = 6;
        private const int InvPlayerW = 32;
        private const int InvPlayerH = 43;

        private const int Scale = 2;

        private const int HotbarGroup = 1;
        private const int MainGroup = 2;
        private const int ArmorGroup = 3;

        private static readonly Vector4D<float> White = new Vector4D<float>(1f, 1f, 1f, 1f);
        private static readonly Vector4D<float> TitleColor = new Vector4D<float>(0.25f, 0.25f, 0.25f, 1f);

        private enum TabKind { Category, Search, Inventory }

        private sealed class TabDef
        {
            public TabKind Kind;
            public CreativeTab Category;
            public bool Top;
            public int Column;
            public bool AlignedRight;
            public string TitleKey;
            public ushort IconId;
            public string IconTexture;
        }

        private readonly WorldClient _world;
        private readonly int _bgX;
        private readonly int _bgY;

        private readonly Dictionary<CreativeTab, List<ushort>> _byTab = new Dictionary<CreativeTab, List<ushort>>();
        private readonly List<Entry> _allItems = new List<Entry>();
        private readonly List<ushort> _searchResults = new List<ushort>();
        private List<ushort> _currentList;

        private readonly List<TabDef> _tabs = new List<TabDef>();
        private int _selected;

        private readonly List<Slot> _gridSlots = new List<Slot>();
        private readonly List<Slot> _inventorySlots = new List<Slot>();

        private string _search = "";
        private bool _searchFocused;
        private bool _acceptChars;
        private float _scrollOffs;
        private bool _scrolling;

        public GuiCreativeInventory(WorldClient world) : base()
        {
            _world = world;

            _bgX = ((int) ScaledResolution.GuiResolution.X - BgWidth * Scale) / 2;
            _bgY = ((int) ScaledResolution.GuiResolution.Y - BgHeight * Scale) / 2;

            BucketItems();
            BuildTabs();
            BuildGridSlots();
            BuildInventorySlots();
            SelectTab(0);
        }

        private TabDef Current => _tabs[_selected];
        private bool IsInventory => Current.Kind == TabKind.Inventory;
        private bool IsSearch => Current.Kind == TabKind.Search;
        private bool ShowsGrid => !IsInventory;

        protected override IReadOnlyList<Slot> Slots => IsInventory ? _inventorySlots : _gridSlots;

        private void BucketItems()
        {
            foreach (var item in GameRegistry.Items)
            {
                if (!_byTab.TryGetValue(item.CreativeTab, out var list))
                {
                    list = new List<ushort>();
                    _byTab[item.CreativeTab] = list;
                }
                list.Add(item.Id);
                _allItems.Add(new Entry(item.Id, SearchKey(item)));
            }
        }

        private void BuildTabs()
        {
            AddCategory(CreativeTab.BuildingBlocks, true, 0, "itemGroup.buildingBlocks", "minecraft:bricks");
            AddCategory(CreativeTab.ColoredBlocks, true, 1, "itemGroup.coloredBlocks", "minecraft:cyan_wool");
            AddCategory(CreativeTab.NaturalBlocks, true, 2, "itemGroup.natural", "minecraft:grass_block");
            AddCategory(CreativeTab.FunctionalBlocks, true, 3, "itemGroup.functional", "minecraft:crafting_table");
            AddCategory(CreativeTab.Redstone, true, 4, "itemGroup.redstone", "minecraft:redstone");
            AddCategory(CreativeTab.ToolsAndUtilities, false, 0, "itemGroup.tools", "minecraft:diamond_pickaxe");
            AddCategory(CreativeTab.Combat, false, 1, "itemGroup.combat", "minecraft:diamond_sword");
            AddCategory(CreativeTab.FoodAndDrink, false, 2, "itemGroup.foodAndDrink", "minecraft:golden_apple");
            AddCategory(CreativeTab.Ingredients, false, 3, "itemGroup.ingredients", "minecraft:iron_ingot");
            AddCategory(CreativeTab.SpawnEggs, false, 4, "itemGroup.spawnEggs", "minecraft:pig_spawn_egg");

            // The search tab's icon is a compass; we have no compass item, so draw frame 16 of its texture
            // (the red-needle-up resting frame) directly.
            _tabs.Add(new TabDef
            {
                Kind = TabKind.Search, Top = true, Column = 6, AlignedRight = true,
                TitleKey = "itemGroup.search", IconTexture = "minecraft/textures/item/compass_16.png"
            });
            _tabs.Add(new TabDef
            {
                Kind = TabKind.Inventory, Top = false, Column = 6, AlignedRight = true,
                TitleKey = "itemGroup.inventory", IconId = IconByMinecraftId("minecraft:chest", null)
            });
        }

        private void AddCategory(CreativeTab category, bool top, int column, string titleKey, string iconMcId)
        {
            if (!_byTab.TryGetValue(category, out var list) || list.Count == 0) return;
            _tabs.Add(new TabDef
            {
                Kind = TabKind.Category, Category = category, Top = top, Column = column,
                AlignedRight = false, TitleKey = titleKey, IconId = IconByMinecraftId(iconMcId, list)
            });
        }

        /// <summary>The id of the registered item whose Minecraft id matches (the tab icon), falling back to the
        /// tab's first item, or 0 if neither exists — so a missing icon item never breaks the tab.</summary>
        private static ushort IconByMinecraftId(string mcId, List<ushort> fallback)
        {
            foreach (var item in GameRegistry.Items)
                if (item.MinecraftId == mcId) return item.Id;
            return fallback != null && fallback.Count > 0 ? fallback[0] : (ushort) 0;
        }

        private void SelectTab(int index)
        {
            _selected = index;
            _scrollOffs = 0f;
            var tab = _tabs[index];

            if (tab.Kind == TabKind.Search)
            {
                _searchFocused = true;
                _search = "";
                ApplySearch();
            }
            else
            {
                _searchFocused = false;
                _currentList = tab.Kind == TabKind.Category ? _byTab[tab.Category] : null;
            }
        }

        // --- Slot construction ----------------------------------------------------------------------------

        private void BuildGridSlots()
        {
            for (var row = 0; row < Rows; row++)
                for (var col = 0; col < Columns; col++)
                {
                    var r = row;
                    var c = col;
                    _gridSlots.Add(new Slot(SlotRect(GridX + c * SlotStride, GridY + r * SlotStride),
                        () =>
                        {
                            if (_currentList == null) return ItemStack.Empty;
                            var index = (RowOffset + r) * Columns + c;
                            return index < _currentList.Count ? new ItemStack(_currentList[index], 1) : ItemStack.Empty;
                        }, null)
                    {
                        IsSource = true
                    });
                }

            for (var col = 0; col < Columns; col++)
                AddInventorySlot(_gridSlots, col, GridX + col * SlotStride, HotbarY, HotbarGroup);
        }

        private void BuildInventorySlots()
        {
            AddArmorSlot(0, InvArmorLeftX, InvArmorTopY, ArmorSlot.Helmet);
            AddArmorSlot(1, InvArmorLeftX, InvArmorBottomY, ArmorSlot.Chestplate);
            AddArmorSlot(2, InvArmorRightX, InvArmorTopY, ArmorSlot.Leggings);
            AddArmorSlot(3, InvArmorRightX, InvArmorBottomY, ArmorSlot.Boots);

            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 9; col++)
                    AddInventorySlot(_inventorySlots, 9 + row * 9 + col, GridX + col * SlotStride, InvMainY + row * SlotStride, MainGroup);

            for (var col = 0; col < 9; col++)
                AddInventorySlot(_inventorySlots, col, GridX + col * SlotStride, HotbarY, HotbarGroup);

            // The destroy slot: an infinite empty source, so dropping a held stack onto it discards it (vanilla
            // trash). Its X is part of the tab_inventory.png background.
            _inventorySlots.Add(new Slot(SlotRect(InvDestroyX, InvDestroyY), () => ItemStack.Empty, null) { IsSource = true });
        }

        private void AddInventorySlot(List<Slot> target, int index, int texX, int texY, int group)
        {
            target.Add(new Slot(SlotRect(texX, texY), () => _world.Inventory.Slots[index], v =>
            {
                _world.Inventory.Slots[index] = v;
                _world.SendInventoryAction(index, v);
            }) { Group = group });
        }

        private void AddArmorSlot(int index, int texX, int texY, ArmorSlot slot)
        {
            _inventorySlots.Add(new Slot(SlotRect(texX, texY), () => _world.Inventory.Armor[index], v =>
            {
                _world.Inventory.Armor[index] = v;
                _world.SendInventoryAction(Inventory.ArmorActionBase + index, v);
            })
            {
                Group = ArmorGroup,
                CanAccept = stack => stack.Item?.ArmorSlot == slot
            });
        }

        protected override void OnShiftClick(Slot slot)
        {
            if (IsInventory)
            {
                if (slot.Group == ArmorGroup)
                {
                    var inv = new List<Slot>(SlotsInGroup(MainGroup));
                    inv.AddRange(SlotsInGroup(HotbarGroup));
                    slot.Set(MergeInto(slot.Get(), inv));
                }
                else if (slot.Group == MainGroup || slot.Group == HotbarGroup)
                    slot.Set(MergeInto(slot.Get(), SlotsInGroup(slot.Group == HotbarGroup ? MainGroup : HotbarGroup)));
                return;
            }

            // Item list → full stack to the hotbar; hotbar slot → trashed (the creative void), like vanilla.
            if (slot.IsSource)
            {
                var give = slot.Get();
                if (give.IsEmpty) return;
                MergeInto(give.WithCount(give.Item?.MaxStackSize ?? ItemStack.MaxStackSize), SlotsInGroup(HotbarGroup));
            }
            else
                slot.Set(ItemStack.Empty);
        }

        // --- Search ---------------------------------------------------------------------------------------

        private static string SearchKey(Item item)
        {
            var name = item.GetName() ?? "";
            var key = item.RegistryKey ?? "";
            var mc = item.MinecraftId ?? "";
            return (name + " " + key + " " + mc).ToLowerInvariant();
        }

        private void ApplySearch()
        {
            _searchResults.Clear();
            if (_search.Length == 0)
                foreach (var e in _allItems) _searchResults.Add(e.Id);
            else
            {
                var q = _search.ToLowerInvariant();
                foreach (var e in _allItems)
                    if (e.Search.Contains(q)) _searchResults.Add(e.Id);
            }

            _currentList = _searchResults;
            _scrollOffs = 0f;
        }

        public override void OnCharTyped(char c)
        {
            if (!_acceptChars || !IsSearch || !_searchFocused || char.IsControl(c) || _search.Length >= MaxSearchLength) return;
            _search += c;
            ApplySearch();
        }

        public override void OnKeyDown(Key key)
        {
            if (key == Key.Escape)
            {
                Close();
                return;
            }

            if (IsSearch && _searchFocused)
            {
                if (key == Key.Backspace && _search.Length > 0)
                {
                    _search = _search.Substring(0, _search.Length - 1);
                    ApplySearch();
                }
                return;
            }

            if (Keybinds.Matches(GameAction.Inventory, key)) Close();
        }

        // --- Scrolling ------------------------------------------------------------------------------------

        private int TotalRows => _currentList == null ? 0 : (_currentList.Count + Columns - 1) / Columns;
        private int HiddenRows => Math.Max(0, TotalRows - Rows);
        private bool CanScroll => ShowsGrid && HiddenRows > 0;
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
                for (var i = 0; i < _tabs.Count; i++)
                    if (TabHit(_tabs[i], guiPos))
                    {
                        if (i != _selected) SelectTab(i);
                        return;
                    }

                _searchFocused = IsSearch && InSearchBox(guiPos);
                if (CanScroll && InScrollColumn(guiPos))
                {
                    _scrolling = true;
                    DragScrollTo(guiPos.Y);
                    return;
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
            _scrollOffs = Math.Clamp((relY - ScrollTop - KnobH / 2f) / ScrollDragTravel, 0f, 1f);
        }

        private bool InSearchBox(Vector2D<float> p)
            => p.X >= _bgX + SearchHitX0 * Scale && p.X <= _bgX + SearchHitX1 * Scale &&
               p.Y >= _bgY + SearchBoxY * Scale && p.Y <= _bgY + (SearchBoxY + SearchBoxH) * Scale;

        private bool InScrollColumn(Vector2D<float> p)
            => p.X >= _bgX + ScrollX * Scale && p.X <= _bgX + (ScrollX + KnobW) * Scale &&
               p.Y >= _bgY + ScrollTop * Scale && p.Y <= _bgY + (ScrollTop + ScrollRenderTravel + KnobH) * Scale;

        // --- Tab geometry ---------------------------------------------------------------------------------

        private int TabRelX(TabDef tab)
            => tab.AlignedRight ? BgWidth - TabSpacing * (7 - tab.Column) + 1 : TabSpacing * tab.Column;

        private int TabGuiX(TabDef tab) => _bgX + TabRelX(tab) * Scale;
        private int TabGuiY(TabDef tab) => tab.Top ? _bgY - TabTopOffset * Scale : _bgY + TabBottomOffset * Scale;

        private bool TabHit(TabDef tab, Vector2D<float> p)
        {
            var x = TabGuiX(tab) + TabHitInsetX * Scale;
            var y = TabGuiY(tab) + TabHitInsetY * Scale;
            return p.X >= x && p.X < x + TabHitW * Scale && p.Y >= y && p.Y < y + TabHitH * Scale;
        }

        // --- Drawing --------------------------------------------------------------------------------------

        protected override void DrawBackground()
        {
            for (var i = 0; i < _tabs.Count; i++)
                if (i != _selected) DrawTab(_tabs[i], false);

            DrawPanel();
            DrawTab(Current, true);

            if (IsInventory) DrawPlayerModel();
            if (!IsInventory) DrawTitle();
            if (IsSearch) DrawSearchText();
            if (ShowsGrid) DrawScroller();
        }

        private void DrawPlayerModel()
        {
            var icon = ItemIconRenderer.GetPlayerIcon();
            if (icon == null) return;
            GuiRenderer.DrawTexture(icon,
                Rectangle.FromSize(_bgX + InvPlayerX * Scale, _bgY + InvPlayerY * Scale, InvPlayerW * Scale, InvPlayerH * Scale),
                null);
        }

        public override void Render()
        {
            base.Render();

            var mouse = ScaledResolution.ToGuiCoords(ClientResources.Input.MousePosition);
            for (var i = 0; i < _tabs.Count; i++)
                if (TabHit(_tabs[i], mouse))
                {
                    DrawTextTooltip(I18N.Get(_tabs[i].TitleKey), mouse);
                    break;
                }
        }

        private void DrawPanel()
        {
            var path = IsSearch ? GuiAssets.CreativeSearchTab : IsInventory ? GuiAssets.CreativeInventoryTab : GuiAssets.CreativeItemsTab;
            var bg = GuiAssets.Get(path);
            if (bg != null)
            {
                GuiRenderer.DrawTexture(bg, Rectangle.FromSize(_bgX, _bgY, BgWidth * Scale, BgHeight * Scale),
                    new Rectangle(0, 0, BgWidth, BgHeight));
                return;
            }

            GuiRenderer.DrawTexture(ClientResources.WhitePixel,
                Rectangle.FromSize(_bgX, _bgY, BgWidth * Scale, BgHeight * Scale), null,
                new Vector4D<float>(0.15f, 0.15f, 0.15f, 0.95f));
            foreach (var slot in Slots)
                GuiRenderer.DrawTexture(ClientResources.WhitePixel, slot.Bounds, null,
                    new Vector4D<float>(0.36f, 0.36f, 0.36f, 1f));
        }

        private void DrawTab(TabDef tab, bool selected)
        {
            var x = TabGuiX(tab);
            var y = TabGuiY(tab);
            var sprite = GuiAssets.Get(GuiAssets.CreativeTabSprite(tab.Top, selected, tab.Column + 1));
            if (sprite != null)
                GuiRenderer.DrawTexture(sprite, Rectangle.FromSize(x, y, TabW * Scale, TabH * Scale), null);
            else
            {
                var shade = selected ? 0.5f : 0.32f;
                GuiRenderer.DrawTexture(ClientResources.WhitePixel, Rectangle.FromSize(x, y, TabW * Scale, TabH * Scale),
                    null, new Vector4D<float>(shade, shade, shade, 1f));
            }

            var iconX = x + TabIconX * Scale;
            var iconY = y + (TabIconY + (tab.Top ? 1 : -1)) * Scale;
            var iconRect = Rectangle.FromSize(iconX, iconY, SlotSize * Scale, SlotSize * Scale);
            if (tab.IconTexture != null)
            {
                var tex = GuiAssets.Get(tab.IconTexture);
                if (tex != null) GuiRenderer.DrawTexture(tex, iconRect, null);
            }
            else if (tab.IconId != 0)
                ItemStackRenderer.Draw(new ItemStack(tab.IconId, 1), iconRect);
        }

        private void DrawTitle()
        {
            Font.DrawString(I18N.Get(Current.TitleKey), _bgX + 8 * Scale, _bgY + 6 * Scale, Scale, TitleColor, false);
        }

        private void DrawSearchText()
        {
            var textX = _bgX + SearchTextX * Scale;
            var textY = _bgY + SearchBoxY * Scale + (SearchBoxH * Scale - Font.LineHeight(Scale)) / 2;

            Font.DrawString(_search, textX, textY, Scale, White);
            if (_searchFocused && CaretVisible())
            {
                var caretX = textX + Font.MeasureWidth(_search, Scale);
                Font.DrawString("|", caretX, textY, Scale, White);
            }
        }

        private void DrawScroller()
        {
            var knobY = _bgY + (ScrollTop + (int) (_scrollOffs * ScrollRenderTravel)) * Scale;
            var rect = Rectangle.FromSize(_bgX + ScrollX * Scale, knobY, KnobW * Scale, KnobH * Scale);

            var sprite = GuiAssets.Get(CanScroll ? GuiAssets.Scroller : GuiAssets.ScrollerDisabled);
            if (sprite != null)
                GuiRenderer.DrawTexture(sprite, rect, null);
            else
                GuiRenderer.DrawTexture(ClientResources.WhitePixel, rect, null,
                    CanScroll ? new Vector4D<float>(0.75f, 0.75f, 0.75f, 1f) : new Vector4D<float>(0.4f, 0.4f, 0.4f, 1f));
        }

        private static void DrawTextTooltip(string text, Vector2D<float> mouse)
        {
            var w = Font.MeasureWidth(text, Scale);
            var h = Font.LineHeight(Scale);
            var x = (int) mouse.X + 10;
            var y = (int) mouse.Y - 10;
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, Rectangle.FromSize(x - 4, y - 4, w + 8, h + 8), null,
                new Vector4D<float>(0.05f, 0f, 0.1f, 0.9f));
            Font.DrawString(text, x, y, Scale, White);
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

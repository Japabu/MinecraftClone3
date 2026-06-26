using System;
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
    /// The furnace screen (opened by right-clicking a furnace), over the official <c>container/furnace.png</c>.
    /// The three furnace slots (input, fuel, output) plus the burn/cook progress live on the server: the screen
    /// reads them from the block's <see cref="ContainerView"/> (streamed each tick) and mirrors slot edits up
    /// with <c>ContainerSlotPacket</c>s, while the player inventory rows behave exactly as the other screens.
    /// </summary>
    public class GuiFurnace : ContainerScreen
    {
        // furnace.png pixel layout (the container region is 176x166; progress sprites live to the right at x=176).
        private const int GuiW = 176;
        private const int GuiH = 166;
        private const int InputX = 56, InputY = 17;
        private const int FuelX = 56, FuelY = 53;
        private const int OutputX = 116, OutputY = 35;
        // Progress sprites (own PNGs under gui/sprites/container/furnace): the lit flame is 14x14 and fills
        // bottom-up; the cook arrow (burn_progress.png) is 24x16 and fills left-to-right.
        private const int FlameX = 56, FlameY = 36, FlameSize = 14;
        private const int ArrowX = 79, ArrowY = 34, ArrowW = 24, ArrowH = 16;
        private const int InvX = 8;
        private const int InvY = 84;
        private const int HotbarY = 142;
        private const int SlotStride = 18;
        private const int SlotSize = 16;
        private const int Scale = 2;

        private const int FurnaceGroup = 1;
        private const int OutputGroup = 2;
        private const int MainGroup = 3;
        private const int HotbarGroup = 4;

        private readonly WorldClient _world;
        private readonly Vector3i _pos;
        private readonly ContainerView _view;
        private readonly List<Slot> _slots = new List<Slot>();

        private readonly int _bgX;
        private readonly int _bgY;

        public GuiFurnace(GameWindow window, WorldClient world, Vector3i pos) : base(window)
        {
            _world = world;
            _pos = pos;
            _view = world.OpenContainer(pos, 3, 4);

            _bgX = ((int) ScaledResolution.GuiResolution.X - GuiW * Scale) / 2;
            _bgY = ((int) ScaledResolution.GuiResolution.Y - GuiH * Scale) / 2;

            BuildSlots();
        }

        protected override IReadOnlyList<Slot> Slots => _slots;

        private void BuildSlots()
        {
            _slots.Add(FurnaceSlot(BlockDataFurnaceSlot.Input, InputX, InputY, FurnaceGroup));
            _slots.Add(FurnaceSlot(BlockDataFurnaceSlot.Fuel, FuelX, FuelY, FurnaceGroup));

            _slots.Add(new Slot(SlotRect(OutputX, OutputY), () => _view.Slots[BlockDataFurnaceSlot.Output], null)
            {
                IsOutput = true,
                OnTakeOutput = () => SetFurnaceSlot(BlockDataFurnaceSlot.Output, ItemStack.Empty),
                Group = OutputGroup
            });

            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 9; col++)
                    AddInventorySlot(9 + row * 9 + col, InvX + col * SlotStride, InvY + row * SlotStride, MainGroup);

            for (var col = 0; col < 9; col++)
                AddInventorySlot(col, InvX + col * SlotStride, HotbarY, HotbarGroup);
        }

        private Slot FurnaceSlot(int index, int texX, int texY, int group)
            => new Slot(SlotRect(texX, texY), () => _view.Slots[index], v => SetFurnaceSlot(index, v)) { Group = group };

        private void SetFurnaceSlot(int index, ItemStack stack)
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
            if (slot.Group == OutputGroup || slot.Group == FurnaceGroup)
            {
                // Furnace slot -> player inventory.
                var inv = new List<Slot>(SlotsInGroup(MainGroup));
                inv.AddRange(SlotsInGroup(HotbarGroup));
                var leftover = MergeInto(slot.Get(), inv);
                if (slot.Group == OutputGroup) SetFurnaceSlot(BlockDataFurnaceSlot.Output, leftover);
                else slot.Set(leftover);
                return;
            }

            // Inventory -> furnace: smeltable goes to the input slot, otherwise fuel to the fuel slot.
            var stack = slot.Get();
            if (stack.IsEmpty) return;
            var target = GameRegistry.MatchSmelting(stack) != null || !FurnaceFuel.IsFuel(stack.ItemId)
                ? _slots[BlockDataFurnaceSlot.Input]
                : _slots[BlockDataFurnaceSlot.Fuel];
            slot.Set(MergeInto(stack, new List<Slot> { target }));
        }

        protected override void DrawBackground()
        {
            var bg = GuiAssets.Get(GuiAssets.Furnace);
            if (bg != null)
                GuiRenderer.DrawTexture(bg, Rectangle.FromSize(_bgX, _bgY, GuiW * Scale, GuiH * Scale),
                    new Rectangle(0, 0, GuiW, GuiH));
            else
            {
                GuiRenderer.DrawTexture(ClientResources.WhitePixel, Rectangle.FromSize(_bgX, _bgY, GuiW * Scale, GuiH * Scale),
                    null, new Color4(0.16f, 0.16f, 0.16f, 0.96f));
                foreach (var s in _slots)
                    GuiRenderer.DrawTexture(ClientResources.WhitePixel, s.Bounds, null, new Color4(0.36f, 0.36f, 0.36f, 1f));
            }

            DrawProgress();
        }

        // Overlays the lit flame (burn time remaining, fills bottom-up) and the cook arrow (cook progress, fills
        // left-to-right) from their own sprite PNGs, drawing only the filled portion of each, exactly as vanilla.
        private void DrawProgress()
        {
            var flame = GuiAssets.Get(GuiAssets.FurnaceLit);
            if (flame != null && _view.Fields[1] > 0 && _view.Fields[0] > 0)
            {
                var h = Clamp((int) Math.Ceiling((double) _view.Fields[0] / _view.Fields[1] * FlameSize), 0, FlameSize);
                if (h > 0)
                    GuiRenderer.DrawTexture(flame, Sprite(FlameX, FlameY + (FlameSize - h), FlameSize, h),
                        new Rectangle(0, FlameSize - h, FlameSize, FlameSize));
            }

            var arrow = GuiAssets.Get(GuiAssets.FurnaceBurn);
            if (arrow != null && _view.Fields[3] > 0 && _view.Fields[2] > 0)
            {
                var w = Clamp((int) ((double) _view.Fields[2] / _view.Fields[3] * ArrowW), 0, ArrowW);
                if (w > 0)
                    GuiRenderer.DrawTexture(arrow, Sprite(ArrowX, ArrowY, w, ArrowH),
                        new Rectangle(0, 0, w, ArrowH));
            }
        }

        protected override void OnClosed() => _world.CloseContainer(_pos);

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        private Rectangle Sprite(int texX, int texY, int w, int h)
            => Rectangle.FromSize(_bgX + texX * Scale, _bgY + texY * Scale, w * Scale, h * Scale);

        private Rectangle SlotRect(int texX, int texY)
            => Rectangle.FromSize(_bgX + texX * Scale, _bgY + texY * Scale, SlotSize * Scale, SlotSize * Scale);
    }

    // Mirrors VanillaPlugin's BlockDataFurnace slot order. Kept here (not referenced from the plugin) so the
    // API screen need not depend on the content plugin; the indices are the container's stable contract.
    internal static class BlockDataFurnaceSlot
    {
        public const int Input = 0;
        public const int Fuel = 1;
        public const int Output = 2;
    }
}

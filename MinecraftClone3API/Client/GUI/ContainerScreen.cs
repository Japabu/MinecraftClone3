using System;
using System.Collections.Generic;
using MinecraftClone3API.Client;
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
    /// Base class for the Minecraft-style container screens (crafting table, creative inventory). It owns the
    /// cursor-held stack and implements the full vanilla slot interaction against the slots a subclass declares:
    /// left-click pick/place/swap/merge, right-click split (pick half) / place-one, and click-drag to distribute
    /// a held stack evenly (left) or one-per-slot (right). Subclasses provide the slot list and draw the
    /// background; everything else — hit-testing, the cursor, tooltips — is handled here.
    /// </summary>
    public abstract class ContainerScreen : GuiBase
    {
        protected readonly GameWindow Window;

        /// <summary>The size a slot icon (and the cursor) is drawn at, GUI pixels. Default matches a 16px slot
        /// at the standard 2× GUI scale.</summary>
        protected int IconSize = 32;

        protected ItemStack Cursor = ItemStack.Empty;

        private bool _leftDown, _rightDown;

        private MouseButton _activeButton;
        private bool _hasActive;
        private Slot _pressSlot;
        private bool _pressShift;
        private bool _dragActive;
        private readonly List<Slot> _dragSlots = new List<Slot>();

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private const double DoubleClickMs = 250;
        private double _lastLeftClickMs = double.NegativeInfinity;
        private Slot _lastClickSlot;

        protected ContainerScreen(GameWindow window)
        {
            Window = window;
            Window.CursorState = CursorState.Normal;
        }

        /// <summary>The screen's slots, in a stable order (built once by the subclass). Accessors may read
        /// mutable subclass state (e.g. a scroll offset) so the list itself need not be rebuilt.</summary>
        protected abstract IReadOnlyList<Slot> Slots { get; }

        protected abstract void DrawBackground();

        /// <summary>Called when the screen closes (return scratch items to the inventory, etc.).</summary>
        protected virtual void OnClosed() { }

        /// <summary>Mouse-wheel hook (creative inventory scrolling); default ignores it.</summary>
        protected virtual void OnScroll(float delta) { }

        /// <summary>Shift-click quick-move; default no-op. Subclasses route between regions via
        /// <see cref="MergeInto"/> + <see cref="SlotsInGroup"/>.</summary>
        protected virtual void OnShiftClick(Slot slot) { }

        public override void Update(bool focused)
        {
            base.Update(focused);
            if (!focused) return;

            var ks = Window.KeyboardState;
            if (ks.IsKeyPressed(Keys.Escape) || ks.IsKeyPressed(Keys.E))
            {
                Close();
                return;
            }

            var scroll = Window.MouseState.ScrollDelta.Y;
            if (scroll != 0) OnScroll(scroll);

            var shift = ks.IsKeyDown(Keys.LeftShift) || ks.IsKeyDown(Keys.RightShift);
            var pos = ScaledResolution.ToGuiCoords(Window.MouseState.Position);

            var left = Window.MouseState.IsButtonDown(MouseButton.Left);
            var right = Window.MouseState.IsButtonDown(MouseButton.Right);

            if (left && !_leftDown) Press(MouseButton.Left, pos, shift);
            if (right && !_rightDown) Press(MouseButton.Right, pos, shift);

            if (_hasActive) AddDragSlot(pos);

            if (!left && _leftDown && _hasActive && _activeButton == MouseButton.Left) Release(pos);
            if (!right && _rightDown && _hasActive && _activeButton == MouseButton.Right) Release(pos);

            _leftDown = left;
            _rightDown = right;
        }

        private void Press(MouseButton button, Vector2 pos, bool shift)
        {
            if (_hasActive) return; // ignore the other button while one gesture is in progress

            _hasActive = true;
            _activeButton = button;
            _pressSlot = SlotAt(pos);
            _pressShift = shift;
            _dragSlots.Clear();

            _dragActive = !Cursor.IsEmpty && _pressSlot != null && IsDragEligible(_pressSlot);
            if (_dragActive) _dragSlots.Add(_pressSlot);
        }

        private void AddDragSlot(Vector2 pos)
        {
            if (!_dragActive) return;
            var slot = SlotAt(pos);
            if (slot == null || _dragSlots.Contains(slot) || !IsDragEligible(slot)) return;
            _dragSlots.Add(slot);
        }

        private void Release(Vector2 pos)
        {
            if (_dragActive && _dragSlots.Count >= 2)
                PerformDrag();
            else if (_pressShift)
            {
                if (_pressSlot != null) OnShiftClick(_pressSlot);
            }
            else if (_activeButton == MouseButton.Left && IsDoubleClick(_pressSlot) && !Cursor.IsEmpty)
                GatherToCursor();
            else
                NormalClick(_activeButton, _pressSlot);

            if (_activeButton == MouseButton.Left && !_pressShift)
            {
                _lastLeftClickMs = Clock.Elapsed.TotalMilliseconds;
                _lastClickSlot = _pressSlot;
            }

            _hasActive = false;
            _dragActive = false;
            _dragSlots.Clear();
        }

        private bool IsDoubleClick(Slot slot) =>
            slot != null && slot == _lastClickSlot &&
            Clock.Elapsed.TotalMilliseconds - _lastLeftClickMs < DoubleClickMs;

        /// <summary>Double-click gather: sweep the container pulling matching items into the held cursor up to a
        /// full stack — partial stacks first so full ones are left intact (vanilla order). Skips output/source
        /// slots and any without a setter.</summary>
        private void GatherToCursor()
        {
            var max = MaxStack(Cursor);
            if (Cursor.Count >= max) return;

            for (var pass = 0; pass < 2; pass++)
            {
                var slots = Slots;
                for (var i = 0; i < slots.Count; i++)
                {
                    var slot = slots[i];
                    if (slot.Set == null || slot.IsOutput || slot.IsSource) continue;
                    var s = slot.Get();
                    if (s.IsEmpty || !s.SameItem(Cursor)) continue;
                    if (pass == 0 && s.Count >= MaxStack(s)) continue;

                    var take = Math.Min(max - Cursor.Count, s.Count);
                    if (take <= 0) continue;
                    Cursor = Cursor.WithCount(Cursor.Count + take);
                    slot.Set(Dec(s, take));
                    if (Cursor.Count >= max) return;
                }
            }
        }

        private void NormalClick(MouseButton button, Slot slot)
        {
            if (slot == null) return;

            if (slot.IsSource)
            {
                ClickSource(button, slot);
                return;
            }

            if (slot.IsOutput)
            {
                TakeOutput(slot);
                return;
            }

            if (button == MouseButton.Left) LeftClick(slot);
            else RightClick(slot);
        }

        private void ClickSource(MouseButton button, Slot slot)
        {
            var item = slot.Get();
            if (item.IsEmpty)
            {
                Cursor = ItemStack.Empty;
                return;
            }

            if (!Cursor.IsEmpty)
            {
                // Holding a stack and clicking the infinite list deletes (some of) it, like the vanilla trash.
                if (button == MouseButton.Left) Cursor = ItemStack.Empty;
                else Cursor = Dec(Cursor);
                return;
            }

            Cursor = button == MouseButton.Left
                ? item.WithCount(MaxStack(item))
                : item.WithCount(1);
        }

        private void LeftClick(Slot slot)
        {
            var s = slot.Get();
            if (Cursor.IsEmpty)
            {
                Cursor = s;
                slot.Set(ItemStack.Empty);
            }
            else if (s.IsEmpty)
            {
                slot.Set(Cursor);
                Cursor = ItemStack.Empty;
            }
            else if (s.SameItem(Cursor))
            {
                var move = Math.Min(MaxStack(s) - s.Count, Cursor.Count);
                if (move <= 0) return;
                slot.Set(s.WithCount(s.Count + move));
                Cursor = Dec(Cursor, move);
            }
            else
            {
                slot.Set(Cursor);
                Cursor = s;
            }
        }

        private void RightClick(Slot slot)
        {
            var s = slot.Get();
            if (Cursor.IsEmpty)
            {
                if (s.IsEmpty) return;
                var take = (s.Count + 1) / 2;
                Cursor = s.WithCount(take);
                slot.Set(Dec(s, take));
            }
            else if (s.IsEmpty)
            {
                slot.Set(Cursor.WithCount(1));
                Cursor = Dec(Cursor);
            }
            else if (s.SameItem(Cursor) && s.Count < MaxStack(s))
            {
                slot.Set(s.WithCount(s.Count + 1));
                Cursor = Dec(Cursor);
            }
        }

        private void TakeOutput(Slot slot)
        {
            var r = slot.Get();
            if (r.IsEmpty) return;

            if (Cursor.IsEmpty)
            {
                Cursor = r;
                slot.OnTakeOutput?.Invoke();
            }
            else if (Cursor.SameItem(r) && Cursor.Count + r.Count <= MaxStack(r))
            {
                Cursor = Cursor.WithCount(Cursor.Count + r.Count);
                slot.OnTakeOutput?.Invoke();
            }
        }

        private void PerformDrag()
        {
            if (Cursor.IsEmpty) return;

            var gives = ComputeDragDistribution(out var remaining);
            foreach (var entry in gives)
            {
                var cur = entry.Key.Get();
                var existing = cur.IsEmpty ? 0 : cur.Count;
                entry.Key.Set(new ItemStack(Cursor.ItemId, existing + entry.Value, Cursor.Metadata));
            }

            Cursor = remaining > 0 ? Cursor.WithCount(remaining) : ItemStack.Empty;
        }

        /// <summary>The per-slot amounts a release would deposit across the painted slots, and (out) the count
        /// left on the cursor afterward. Drives both the actual deposit and the live drag preview so they agree.</summary>
        private Dictionary<Slot, int> ComputeDragDistribution(out int cursorRemaining)
        {
            var gives = new Dictionary<Slot, int>();
            cursorRemaining = Cursor.Count;
            if (Cursor.IsEmpty || _dragSlots.Count == 0) return gives;

            var max = MaxStack(Cursor);
            var per = _activeButton == MouseButton.Left ? Math.Max(1, Cursor.Count / _dragSlots.Count) : 1;
            var remaining = Cursor.Count;

            foreach (var slot in _dragSlots)
            {
                if (remaining <= 0) break;
                var cur = slot.Get();
                var existing = cur.IsEmpty ? 0 : cur.Count;
                var give = Math.Min(Math.Min(per, max - existing), remaining);
                if (give <= 0) continue;
                gives[slot] = give;
                remaining -= give;
            }

            cursorRemaining = remaining;
            return gives;
        }

        private bool IsDragEligible(Slot slot)
        {
            if (slot.Set == null || slot.IsOutput || slot.IsSource) return false;
            var s = slot.Get();
            return s.IsEmpty || (s.SameItem(Cursor) && s.Count < MaxStack(s));
        }

        private Slot SlotAt(Vector2 pos)
        {
            var slots = Slots;
            for (var i = 0; i < slots.Count; i++)
                if (slots[i].Contains(pos)) return slots[i];
            return null;
        }

        /// <summary>Shift-click quick-move helper: merge <paramref name="stack"/> into <paramref name="targets"/>
        /// — first topping up matching stacks, then filling empty slots — and return whatever didn't fit.
        /// Output/source/read-only targets are skipped; each write goes through the slot's setter (so server
        /// mirroring happens automatically).</summary>
        protected ItemStack MergeInto(ItemStack stack, IReadOnlyList<Slot> targets)
        {
            if (stack.IsEmpty) return stack;

            for (var i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t.Set == null || t.IsOutput || t.IsSource) continue;
                var d = t.Get();
                if (d.IsEmpty || !d.SameItem(stack)) continue;
                var cap = MaxStack(d) - d.Count;
                if (cap <= 0) continue;
                var move = Math.Min(cap, stack.Count);
                t.Set(d.WithCount(d.Count + move));
                stack = Dec(stack, move);
                if (stack.IsEmpty) return stack;
            }

            for (var i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t.Set == null || t.IsOutput || t.IsSource) continue;
                if (!t.Get().IsEmpty) continue;
                t.Set(stack);
                return ItemStack.Empty;
            }

            return stack;
        }

        /// <summary>The slots tagged with the given <see cref="Slot.Group"/>, in declaration order.</summary>
        protected IReadOnlyList<Slot> SlotsInGroup(int group)
        {
            var result = new List<Slot>();
            var slots = Slots;
            for (var i = 0; i < slots.Count; i++)
                if (slots[i].Group == group) result.Add(slots[i]);
            return result;
        }

        private static int MaxStack(ItemStack s) => s.Item?.MaxStackSize ?? ItemStack.MaxStackSize;

        private static ItemStack Dec(ItemStack s, int by = 1) =>
            s.Count - by <= 0 ? ItemStack.Empty : s.WithCount(s.Count - by);

        public override void Render()
        {
            SetBlend();

            var screen = Window.FramebufferSize;
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, new Rectangle(0, 0, screen.X, screen.Y), null,
                new Color4(0f, 0f, 0f, 0.4f), false);

            DrawBackground();

            var mouse = ScaledResolution.ToGuiCoords(Window.MouseState.Position);

            // While painting, mirror what a release would deposit into each slot so the items appear in place
            // and the cursor shows only the remainder.
            Dictionary<Slot, int> dragGives = null;
            var cursorRemaining = Cursor.Count;
            if (_dragActive && _dragSlots.Count >= 2)
                dragGives = ComputeDragDistribution(out cursorRemaining);

            var slots = Slots;
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                var stack = slot.Get();
                if (dragGives != null && dragGives.TryGetValue(slot, out var give))
                {
                    var existing = stack.IsEmpty ? 0 : stack.Count;
                    stack = new ItemStack(Cursor.ItemId, existing + give, Cursor.Metadata);
                }
                ItemStackRenderer.Draw(stack, slot.Bounds);
            }

            // Slot highlight: the painted slots while dragging, otherwise the slot under the cursor.
            if (dragGives != null)
            {
                foreach (var slot in _dragSlots)
                    DrawSlotHighlight(slot.Bounds);
            }
            else
            {
                var hovered = SlotAt(mouse);
                if (hovered != null) DrawSlotHighlight(hovered.Bounds);
            }

            if (!Cursor.IsEmpty)
            {
                var carried = dragGives != null ? Cursor.WithCount(cursorRemaining) : Cursor;
                if (!carried.IsEmpty)
                    ItemStackRenderer.Draw(carried,
                        Rectangle.FromSize((int) mouse.X - IconSize / 2, (int) mouse.Y - IconSize / 2, IconSize, IconSize));
            }
            else
            {
                var hovered = SlotAt(mouse);
                if (hovered != null) GuiTooltip.Draw(hovered.Get(), mouse);
            }
        }

        /// <summary>The vanilla translucent-white slot hover/paint overlay (<c>0x80FFFFFF</c>).</summary>
        private static void DrawSlotHighlight(Rectangle bounds) =>
            GuiRenderer.DrawTexture(ClientResources.WhitePixel, bounds, null,
                new Color4(1f, 1f, 1f, 0.5f));

        protected static void SetBlend() => RenderState.Set(new GlState
        {
            Blend = true,
            BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
        });

        protected void Close()
        {
            OnClosed();
            Window.CursorState = CursorState.Grabbed;
            PlayerController.ResetMouse();
            IsDead = true;
        }
    }
}

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

        /// <summary>Shift-click quick-move; default no-op. Subclasses with a meaningful destination override.</summary>
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
            else
                NormalClick(_activeButton, _pressSlot);

            _hasActive = false;
            _dragActive = false;
            _dragSlots.Clear();
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

            var n = _dragSlots.Count;
            var max = MaxStack(Cursor);
            var per = _activeButton == MouseButton.Left ? Math.Max(1, Cursor.Count / n) : 1;
            var remaining = Cursor.Count;

            foreach (var slot in _dragSlots)
            {
                if (remaining <= 0) break;
                var cur = slot.Get();
                var existing = cur.IsEmpty ? 0 : cur.Count;
                var give = Math.Min(Math.Min(per, max - existing), remaining);
                if (give <= 0) continue;
                slot.Set(new ItemStack(Cursor.ItemId, existing + give, Cursor.Metadata));
                remaining -= give;
            }

            Cursor = remaining > 0 ? Cursor.WithCount(remaining) : ItemStack.Empty;
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

            var slots = Slots;
            for (var i = 0; i < slots.Count; i++)
                ItemStackRenderer.Draw(slots[i].Get(), slots[i].Bounds);

            var mouse = ScaledResolution.ToGuiCoords(Window.MouseState.Position);
            if (!Cursor.IsEmpty)
                ItemStackRenderer.Draw(Cursor,
                    Rectangle.FromSize((int) mouse.X - IconSize / 2, (int) mouse.Y - IconSize / 2, IconSize, IconSize));
            else
            {
                var hovered = SlotAt(mouse);
                if (hovered != null) GuiTooltip.Draw(hovered.Get(), mouse);
            }
        }

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

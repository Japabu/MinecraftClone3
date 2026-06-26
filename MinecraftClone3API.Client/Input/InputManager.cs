using System;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.Input
{
    /// <summary>
    /// The client's input source, wrapping Silk.NET's <see cref="IKeyboard"/>/<see cref="IMouse"/> directly.
    ///
    /// <para><b>Event-driven for discrete actions:</b> consumers subscribe to <see cref="KeyDown"/>/
    /// <see cref="MouseDown"/>/<see cref="CharTyped"/>/<see cref="Scroll"/> (forwarded straight from Silk) for
    /// one-shot actions — menu toggles, hotbar keys, text entry, button clicks. <b>Level queries for
    /// continuous input:</b> <see cref="IsKeyDown"/>/<see cref="IsMouseDown"/> read Silk's own held state for
    /// movement, and <see cref="ConsumeMouseDelta"/> returns the look delta accumulated since the last frame.
    /// Silk's <see cref="Key"/>/<see cref="MouseButton"/> are used as-is, with no intermediate snapshot structs.</para>
    /// </summary>
    public sealed class InputManager
    {
        private readonly IInputContext _context;
        private IKeyboard _keyboard;
        private IMouse _mouse;

        /// <summary>A key was pressed this event (rising edge), Silk's <see cref="IKeyboard.KeyDown"/>.</summary>
        public event Action<Key> KeyDown;
        public event Action<Key> KeyUp;
        /// <summary>A text-entry character (Silk's <see cref="IKeyboard.KeyChar"/>), feeding text inputs.</summary>
        public event Action<char> CharTyped;
        public event Action<MouseButton> MouseDown;
        public event Action<MouseButton> MouseUp;
        /// <summary>Vertical scroll-wheel delta (+ up / − down).</summary>
        public event Action<float> Scroll;

        private Vector2D<float> _mouseDelta;
        private Vector2D<float> _lastMousePosition;
        private bool _haveLastPosition;

        public Vector2D<float> MousePosition { get; private set; }

        public InputManager(IInputContext context)
        {
            _context = context;
            if (_context.Keyboards.Count > 0) BindKeyboard(_context.Keyboards[0]);
            if (_context.Mice.Count > 0) BindMouse(_context.Mice[0]);
            _context.ConnectionChanged += OnConnectionChanged;
        }

        private void OnConnectionChanged(IInputDevice device, bool connected)
        {
            if (connected && device is IKeyboard kb && _keyboard == null) BindKeyboard(kb);
            if (connected && device is IMouse m && _mouse == null) BindMouse(m);
        }

        private void BindKeyboard(IKeyboard kb)
        {
            _keyboard = kb;
            kb.KeyDown += (_, key, _) => KeyDown?.Invoke(key);
            kb.KeyUp += (_, key, _) => KeyUp?.Invoke(key);
            kb.KeyChar += (_, c) => CharTyped?.Invoke(c);
        }

        private void BindMouse(IMouse mouse)
        {
            _mouse = mouse;
            mouse.MouseDown += (_, btn) => MouseDown?.Invoke(btn);
            mouse.MouseUp += (_, btn) => MouseUp?.Invoke(btn);
            mouse.Scroll += (_, wheel) => Scroll?.Invoke(wheel.Y);
            mouse.MouseMove += (_, pos) =>
            {
                var p = new Vector2D<float>(pos.X, pos.Y);
                if (_haveLastPosition) _mouseDelta += p - _lastMousePosition;
                _lastMousePosition = p;
                _haveLastPosition = true;
                MousePosition = p;
            };
        }

        /// <summary>Held this instant (Silk's own level state) — used for continuous input like movement.</summary>
        public bool IsKeyDown(Key key) => _keyboard != null && _keyboard.IsKeyPressed(key);

        public bool IsMouseDown(MouseButton button) => _mouse != null && _mouse.IsButtonPressed(button);

        /// <summary>The mouse-look delta accumulated since the last call, then reset. Called once per frame by
        /// the player controller. Pulling (not snapshotting) keeps every sub-frame move from <see cref="IMouse.MouseMove"/>.</summary>
        public Vector2D<float> ConsumeMouseDelta()
        {
            var d = _mouseDelta;
            _mouseDelta = Vector2D<float>.Zero;
            return d;
        }

        /// <summary>Drop the next accumulated delta (after re-grabbing the cursor) so the camera doesn't snap.</summary>
        public void ResetMouseDelta()
        {
            _mouseDelta = Vector2D<float>.Zero;
            _haveLastPosition = false;
        }

        public CursorMode CursorMode
        {
            get => _mouse?.Cursor.CursorMode ?? CursorMode.Normal;
            set { if (_mouse != null) _mouse.Cursor.CursorMode = value; }
        }
    }
}

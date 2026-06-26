using MinecraftClone3API.Util;
using System.Collections.Generic;

namespace MinecraftClone3API.Client.StateSystem
{
    public static class StateEngine
    {
        private static List<StateBase> _states = new List<StateBase>();
        private static List<StateBase> _overlays = new List<StateBase>();
        private static readonly List<StateBase> _overlaysToRemove = new List<StateBase>();
        private static StateBase _pendingState;

        /// <summary>True while an open overlay declares it pauses the world (the Esc menu). The active state
        /// reads this to freeze the singleplayer simulation; other overlays (inventory, furnace, …) leave it
        /// running. Recomputed every <see cref="Update"/> from the current overlay stack.</summary>
        public static bool WorldPaused { get; private set; }

        public static void Update()
        {
            var stateFocused = _overlays.Count == 0;
            var topOverlay = _overlays.Count > 0 ? _overlays[_overlays.Count - 1] : null;

            WorldPaused = false;
            for (var i = 0; i < _overlays.Count; i++)
                if (_overlays[i].PausesWorld) { WorldPaused = true; break; }

            _overlaysToRemove.Clear();
            for (var i = _overlays.Count - 1; i >= 0; i--)
            {
                var o = _overlays[i];
                o.Update(o == topOverlay);
                if (o.IsDead) _overlaysToRemove.Add(o);
            }
            for (var i = 0; i < _overlaysToRemove.Count; i++)
                _overlays.Remove(_overlaysToRemove[i]);

            var last = _states.Count > 0 ? _states[_states.Count - 1] : null;
            if (last != null)
            {
                if (last.IsDead)
                {
                    _states.RemoveAt(_states.Count - 1);
                    last.Exit();
                }
                else last.Update(stateFocused);
            }

            if (_pendingState != null)
            {
                _overlays.ReverseForEach(o => o.Exit());
                _overlays.Clear();
                _states.ReverseForEach(s => s.Exit());
                _states.Clear();
                _states.Add(_pendingState);
                _pendingState = null;
            }
        }

        public static void Render()
        {
            if (_states.Count > 0) _states[_states.Count - 1].Render();
            _overlays.ForEach(s => s.Render());
        }

        public static void Exit()
        {
            _overlays.ReverseForEach(o => o.Exit());
            _states.ReverseForEach(s => s.Exit());
        }

        public static void AddState(StateBase state) => _states.Add(state);
        public static void AddOverlay(StateBase overlay) => _overlays.Add(overlay);

        /// <summary>
        /// Replaces the entire state and overlay stack with <paramref name="state"/> at the end of the
        /// current frame, calling <see cref="StateBase.Exit"/> on every removed layer (so a world saves
        /// via its <c>Exit</c>). Deferred because it is safe to call from within a layer's update.
        /// </summary>
        public static void ReplaceState(StateBase state) => _pendingState = state;
    }
}

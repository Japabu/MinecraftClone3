using MinecraftClone3API.Util;
using System.Collections.Generic;
using System.Linq;

namespace MinecraftClone3API.Client.StateSystem
{
    public static class StateEngine
    {
        private static List<StateBase> _states = new List<StateBase>();
        private static List<StateBase> _overlays = new List<StateBase>();
        private static StateBase _pendingState;

        public static void Update()
        {
            var stateFocused = _overlays.Count == 0;
            var topOverlay = _overlays.LastOrDefault();

            var overlaysToRemove = new List<StateBase>();
            _overlays.ReverseForEach(o =>
            {
                o.Update(o == topOverlay);
                if (o.IsDead) overlaysToRemove.Add(o);
            });
            overlaysToRemove.ForEach(o => _overlays.Remove(o));

            var last = _states.LastOrDefault();
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
            _states.LastOrDefault()?.Render();
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

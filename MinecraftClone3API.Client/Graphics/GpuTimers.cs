using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>Per-pass GPU timing for the deferred renderer, feeding the F3 profiler's shadowMs/geomMs/
    /// compMs columns. Uses GL_TIMESTAMP marker queries (a start + end timestamp per pass) rather than
    /// GL_TIME_ELAPSED: timestamps are point markers, not begin/end-scoped ranges, so they nest freely
    /// inside the whole-frame GL_TIME_ELAPSED query already running in <c>GameClient.OnRenderFrame</c>
    /// (two active GL_TIME_ELAPSED queries would be a GL error).
    ///
    /// Results are harvested from a ring of <see cref="Ring"/> query sets: each frame writes one set and
    /// reads back the newest set whose results have actually arrived. A fixed 1-frame ping-pong is NOT
    /// enough — with vsync off the CPU runs several frames ahead of the GPU, so last frame's query usually
    /// isn't finished yet and the read would perpetually see "not available" and freeze at a stale value.
    /// The ring gives the GPU many frames to finish, and harvest-newest-ready keeps latency at ~1-2 frames
    /// when the GPU keeps up. Everything no-ops unless <see cref="Enabled"/> (set from <c>Profiler.Recording</c>),
    /// so a normal run issues no extra GL.</summary>
    public static class GpuTimers
    {
        public enum Pass { Shadow, Geometry, Composition }

        private const int Count = 3;
        // Deep enough that the GPU is never this many frames behind the CPU even with vsync off (where the
        // present doesn't throttle the CPU). Query objects are tiny, so over-provisioning is free.
        private const int Ring = 8;

        // [slot, pass]
        private static readonly int[,] _start = new int[Ring, Count];
        private static readonly int[,] _end = new int[Ring, Count];
        private static readonly bool[,] _issued = new bool[Ring, Count];
        private static readonly bool[] _pending = new bool[Ring];
        private static readonly long[] _slotFrame = new long[Ring];
        private static readonly double[] _ms = new double[Count];
        private static bool _ready;
        private static long _frame;
        private static long _lastHarvested = -1;

        /// <summary>Gate, set each frame from RenderWorld (= Profiler.Recording). When false every method
        /// returns immediately so no timestamp queries are issued.</summary>
        public static bool Enabled;

        public static double Ms(Pass p) => _ms[(int) p];

        /// <summary>Harvests the newest ring slot whose timestamps have arrived (no stall — only reads results
        /// flagged available) and prepares this frame's write slot. Call once at the top of the world render,
        /// before any <see cref="Begin"/>.</summary>
        public static void BeginFrame()
        {
            if (!Enabled) return;

            if (!_ready)
            {
                for (var s = 0; s < Ring; s++)
                    for (var i = 0; i < Count; i++)
                    {
                        _start[s, i] = GL.GenQuery();
                        _end[s, i] = GL.GenQuery();
                    }
                _ready = true;
            }

            Harvest();

            var write = (int) (_frame % Ring);
            _pending[write] = false;
            for (var i = 0; i < Count; i++) _issued[write, i] = false;
        }

        private static void Harvest()
        {
            var best = -1;
            var bestFrame = _lastHarvested;
            for (var s = 0; s < Ring; s++)
            {
                if (!_pending[s]) continue;
                if (_slotFrame[s] <= _lastHarvested) { _pending[s] = false; continue; }

                var available = true;
                for (var i = 0; i < Count; i++)
                {
                    if (!_issued[s, i]) continue;
                    GL.GetQueryObject(_end[s, i], GetQueryObjectParam.QueryResultAvailable, out int a);
                    if (a == 0) { available = false; break; }
                }
                if (available && _slotFrame[s] > bestFrame) { best = s; bestFrame = _slotFrame[s]; }
            }

            if (best < 0) return;

            for (var i = 0; i < Count; i++)
            {
                if (_issued[best, i])
                {
                    GL.GetQueryObject(_start[best, i], GetQueryObjectParam.QueryResult, out long t0);
                    GL.GetQueryObject(_end[best, i], GetQueryObjectParam.QueryResult, out long t1);
                    _ms[i] = (t1 - t0) / 1_000_000.0;
                }
                else _ms[i] = 0;
            }
            _pending[best] = false;
            _lastHarvested = bestFrame;
        }

        public static void Begin(Pass p)
        {
            if (!Enabled || !_ready) return;
            GL.QueryCounter(_start[(int) (_frame % Ring), (int) p], QueryCounterTarget.Timestamp);
        }

        public static void End(Pass p)
        {
            if (!Enabled || !_ready) return;
            var write = (int) (_frame % Ring);
            GL.QueryCounter(_end[write, (int) p], QueryCounterTarget.Timestamp);
            _issued[write, (int) p] = true;
        }

        public static void EndFrame()
        {
            if (!Enabled) return;
            var write = (int) (_frame % Ring);
            var any = false;
            for (var i = 0; i < Count; i++) any |= _issued[write, i];
            if (any)
            {
                _slotFrame[write] = _frame;
                _pending[write] = true;
            }
            _frame++;
        }
    }
}

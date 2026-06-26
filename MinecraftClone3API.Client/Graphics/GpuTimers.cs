namespace MinecraftClone3API.Graphics
{
    /// <summary>Per-pass GPU timing for the deferred renderer, feeding the F3 profiler's shadowMs/geomMs/
    /// compMs columns.
    ///
    /// <para>Uses WebGPU timestamp queries via a <c>QuerySet</c> resolved into a buffer, but Metal restricts
    /// where timestamps may be written and
    /// the RHI has no <c>QuerySet</c> wrapper yet. Until that lands this is a SAFE NO-OP preserving the public
    /// surface (<see cref="Enabled"/>, the frame/pass hooks, the <see cref="Pass"/> enum, and <see cref="Ms"/>)
    /// so the callers — WorldRenderer's per-pass timing and the RenderDebug overlay — compile and run; every
    /// pass simply reports 0 ms.</para>
    ///
    /// TODO(M7): real WebGPU timestamp queries (QuerySet + ResolveQuerySet → readback buffer).</summary>
    public static class GpuTimers
    {
        public enum Pass { Shadow, Geometry, Composition }

        private const int Count = 3;
        private static readonly double[] _ms = new double[Count];

        /// <summary>Gate, set each frame from RenderWorld (= Profiler.Recording). A no-op while timestamp
        /// queries are unimplemented, kept so callers don't change shape when they're added back.</summary>
        public static bool Enabled;

        /// <summary>Per-pass GPU time in milliseconds. Always 0 until WebGPU timestamp queries are wired up.</summary>
        public static double Ms(Pass p) => _ms[(int) p];

        public static void BeginFrame() { }

        public static void Begin(Pass p) { }

        public static void End(Pass p) { }

        public static void EndFrame() { }
    }
}

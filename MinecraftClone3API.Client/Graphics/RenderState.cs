using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// The complete mutable GL pipeline state a draw depends on. Every field defaults to off
    /// (<c>false</c>/<c>null</c>), so a descriptor always describes the <i>whole</i> state — there
    /// is no inheriting whatever the previous pass happened to leave enabled. <c>default</c> is
    /// "everything off".
    /// </summary>
    public readonly record struct GlState
    {
        public bool Blend { get; init; }
        public (BlendingFactor Src, BlendingFactor Dst)? BlendFunc { get; init; }
        public bool DepthTest { get; init; }
        public DepthFunction? DepthFunc { get; init; }
        public bool CullFace { get; init; }
    }

    /// <summary>
    /// The single source of truth for GL render state. <see cref="Set"/> applies a full
    /// <see cref="GlState"/> and emits only the GL calls that differ from the last applied state,
    /// so a pass can declare exactly what it needs every frame at no cost. Nothing else may toggle
    /// these capabilities directly, or the shadow falls out of sync.
    /// </summary>
    public static class RenderState
    {
        private static readonly (BlendingFactor Src, BlendingFactor Dst) DefaultBlendFunc =
            (BlendingFactor.One, BlendingFactor.Zero);
        private const DepthFunction DefaultDepthFunc = DepthFunction.Less;

        private static GlState _current;
        private static bool _synced;

        public static void Set(GlState state)
        {
            if (_synced && state == _current) return;

            Capability(EnableCap.Blend, state.Blend, _current.Blend);
            Capability(EnableCap.DepthTest, state.DepthTest, _current.DepthTest);
            Capability(EnableCap.CullFace, state.CullFace, _current.CullFace);

            var blendFunc = state.BlendFunc ?? DefaultBlendFunc;
            if (!_synced || blendFunc != (_current.BlendFunc ?? DefaultBlendFunc))
                GL.BlendFunc(blendFunc.Src, blendFunc.Dst);

            var depthFunc = state.DepthFunc ?? DefaultDepthFunc;
            if (!_synced || depthFunc != (_current.DepthFunc ?? DefaultDepthFunc))
                GL.DepthFunc(depthFunc);

            _current = state;
            _synced = true;
        }

        private static void Capability(EnableCap cap, bool desired, bool current)
        {
            if (_synced && desired == current) return;
            if (desired) GL.Enable(cap);
            else GL.Disable(cap);
        }
    }
}

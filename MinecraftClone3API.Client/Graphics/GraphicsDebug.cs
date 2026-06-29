using System;

namespace MinecraftClone3API.Graphics
{
    /// <summary>Debug annotations -- debug groups + object labels -- that make a graphics-debugger frame
    /// capture navigable (passes become collapsible groups, resources get names). In WebGPU the per-pass
    /// groups map to the frame encoder's <c>PushDebugGroup</c>/<c>PopDebugGroup</c>; object labels are set at
    /// resource-creation time in the RHI wrappers (every <c>GpuBuffer</c>/<c>GpuTexture</c>/pipeline takes a
    /// label), so <see cref="Label"/> is a no-op. Groups are issued only when <see cref="Enabled"/>
    /// (RENDERDOC_CAPOPTS, MC3_FORCE_X11=1, or MC3_GL_DEBUG=1), so normal runs pay nothing.</summary>
    public static class GraphicsDebug
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RENDERDOC_CAPOPTS") != null ||
            Environment.GetEnvironmentVariable("MC3_FORCE_X11") == "1" ||
            Environment.GetEnvironmentVariable("MC3_GL_DEBUG") == "1";

        public static void PushGroup(string name)
        {
            if (Enabled) Renderer.Encoder.PushDebugGroup(name);
        }

        public static void PopGroup()
        {
            if (Enabled) Renderer.Encoder.PopDebugGroup();
        }

        /// <summary>Object labels are set at creation in the RHI wrappers, so this is a no-op (kept so callers
        /// that named GPU resources still compile).</summary>
        public static void Label(string name) { }
    }
}

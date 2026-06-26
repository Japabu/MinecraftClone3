using System;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>KHR_debug annotations -- debug groups + object labels -- that make a RenderDoc/apitrace
    /// frame capture navigable (passes become collapsible groups with per-group GPU timing, resources get
    /// names) without any in-engine profiling: RenderDoc still does all the timing, we only label the
    /// command stream. Every call is a no-op unless a graphics debugger is attached (RenderDoc sets
    /// RENDERDOC_CAPOPTS) or MC3_GL_DEBUG=1, so normal runs -- and macOS, which has no KHR_debug -- pay
    /// nothing and never touch the (possibly absent) entry points.</summary>
    public static class GraphicsDebug
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RENDERDOC_CAPOPTS") != null ||
            Environment.GetEnvironmentVariable("MC3_FORCE_X11") == "1" ||
            Environment.GetEnvironmentVariable("MC3_GL_DEBUG") == "1";

        public static void PushGroup(string name)
        {
            if (Enabled) GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 0, name.Length, name);
        }

        public static void PopGroup()
        {
            if (Enabled) GL.PopDebugGroup();
        }

        public static void Label(ObjectLabelIdentifier type, int handle, string name)
        {
            if (Enabled) GL.ObjectLabel(type, handle, name.Length, name);
        }
    }
}

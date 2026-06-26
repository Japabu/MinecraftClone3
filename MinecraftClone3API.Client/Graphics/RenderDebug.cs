namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Central state for the in-world debug tooling: the F-key toggles, the per-frame render stats, and the
    /// frame timings the diagnostics overlay shows. Kept out of <see cref="WorldRenderer"/> so the renderer
    /// stays focused on rendering — <see cref="MinecraftClone3API.Entities.PlayerController"/> flips the
    /// toggles, <see cref="WorldRenderer"/> reads them and writes the stats, and the world state draws the
    /// overlays. (Chunk borders are F4 = <see cref="ChunkBorderRenderer.Enabled"/>, kept on their own renderer.)
    /// </summary>
    public static class RenderDebug
    {
        /// <summary>F1: controls/help overlay (keybind list).</summary>
        public static bool ShowControls;

        /// <summary>F3: live diagnostics overlay (fps, gpu, draw counts, position).</summary>
        public static bool ShowDiagnostics;

        /// <summary>F7: output the raw shadow factor as greyscale (drives composition uDebugShadow).</summary>
        public static bool ShadowFactor;

        // Per-frame stats written by WorldRenderer.BuildVisibleSet / RenderWorld, read by the overlay.
        public static int DrawnChunks;
        public static int LodDrawn;
        public static bool ShadowPass;

        // Per-frame timings written by the game loop (GameClient.OnRenderFrame), read by the overlay.
        // FrameMs is EMA-smoothed so the displayed FPS is steady; GpuMs/UpdateMs are raw last-frame values.
        public static double FrameMs;
        public static double GpuMs;
        public static double UpdateMs;
    }
}

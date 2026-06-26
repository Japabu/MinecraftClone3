using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Silk.NET.Windowing;

namespace MinecraftClone3API.Client
{
    /// <summary>Swapchain present mode. Off = Immediate (no sync, tear), On = Fifo (sync to refresh),
    /// Adaptive = Mailbox (sync, newest-frame, no tear) where supported. Engine-owned; mapped to a wgpu
    /// present mode when the surface is configured.</summary>
    public enum VSyncMode
    {
        Off = 0,
        On = 1,
        Adaptive = 2
    }

    /// <summary>Sun-shadow quality preset. Drives the shadow map resolution + coverage distance; Off skips
    /// the shadow passes entirely.</summary>
    public enum ShadowQuality
    {
        Off = 0,
        Low = 1,
        Medium = 2,
        High = 3
    }

    /// <summary>
    /// Persistent, user-tunable graphics options. <see cref="Load"/> reads the saved values at startup (no
    /// GPU required), and each setter writes the file back. Some options push window-level state onto the live
    /// <see cref="ClientResources.Window"/> (VSync/Fullscreen); the rest are read directly each frame by
    /// <see cref="Graphics.WorldRenderer"/> / the player controller / the world state, so a change takes
    /// effect without an apply step. Numeric setters clamp to their valid range.
    /// </summary>
    public static class GraphicsSettings
    {
        public const int MinRenderDistanceChunks = 4;
        public const int MaxRenderDistanceChunks = 64;
        public const float MinFov = 30f;
        public const float MaxFov = 110f;
        public const float MinMouseSensitivity = 0.001f;
        public const float MaxMouseSensitivity = 0.02f;
        public const float MinBrightness = 0f;
        public const float MaxBrightness = 0.3f;
        // Inside the render distance everything is FULL per-block detail (no within-RD LOD). LOD Horizon = how
        // many chunks the cheap Phase-2 distant horizon extends BEYOND the render distance (0 = no far horizon).
        // LOD Quality scales how far the horizon's detail rings (stride 4/8/16) extend before coarsening: higher
        // = finer horizon farther out (lower FPS), lower = coarser/cheaper.
        public const float MinLodHorizonQuality = 0.5f;
        public const float MaxLodHorizonQuality = 2.0f;
        public const int MinLodHorizonChunks = 0;
        public const int MaxLodHorizonChunks = 96;

        private class Data
        {
            [JsonConverter(typeof(StringEnumConverter))]
            public VSyncMode VSync = VSyncMode.On;
            [JsonConverter(typeof(StringEnumConverter))]
            public ShadowQuality ShadowQuality = ShadowQuality.Medium;
            public bool Fullscreen = false;
            public int RenderDistanceChunks = 10;
            public float Fov = 60f;
            public float MouseSensitivity = 0.008f;
            public float Brightness = 0.01f;
            public float LodHorizonQuality = 1.0f;
            public int LodHorizonChunks = 64;
        }

        private static Data _data = new Data();

        /// <summary>When true, setters apply in-memory but don't persist to disk — the benchmark sets this so
        /// its deterministic overrides (render distance / shadow quality) don't clobber the user's saved file.</summary>
        public static bool SuppressSave;

        public static VSyncMode VSync
        {
            get => _data.VSync;
            set { _data.VSync = value; Save(); ApplyVSync(); }
        }

        public static ShadowQuality ShadowQuality
        {
            get => _data.ShadowQuality;
            set { _data.ShadowQuality = value; Save(); }
        }

        /// <summary>Convenience for the renderer's shadow-pass gate: shadows are on for any quality but Off.</summary>
        public static bool ShadowsEnabled => _data.ShadowQuality != ShadowQuality.Off;

        public static bool Fullscreen
        {
            get => _data.Fullscreen;
            set { _data.Fullscreen = value; Save(); ApplyFullscreen(); }
        }

        public static int RenderDistanceChunks
        {
            get => _data.RenderDistanceChunks;
            set { _data.RenderDistanceChunks = Clamp(value, MinRenderDistanceChunks, MaxRenderDistanceChunks); Save(); }
        }

        public static float Fov
        {
            get => _data.Fov;
            set { _data.Fov = Clamp(value, MinFov, MaxFov); Save(); }
        }

        public static float MouseSensitivity
        {
            get => _data.MouseSensitivity;
            set { _data.MouseSensitivity = Clamp(value, MinMouseSensitivity, MaxMouseSensitivity); Save(); }
        }

        public static float Brightness
        {
            get => _data.Brightness;
            set { _data.Brightness = Clamp(value, MinBrightness, MaxBrightness); Save(); }
        }

        public static float LodHorizonQuality
        {
            get => _data.LodHorizonQuality;
            set { _data.LodHorizonQuality = Clamp(value, MinLodHorizonQuality, MaxLodHorizonQuality); Save(); }
        }

        public static int LodHorizonChunks
        {
            get => _data.LodHorizonChunks;
            set { _data.LodHorizonChunks = Clamp(value, MinLodHorizonChunks, MaxLodHorizonChunks); Save(); }
        }

        public static void Load()
        {
            if (!File.Exists(GamePaths.GraphicsSettingsFile)) return;
            try
            {
                _data = JsonConvert.DeserializeObject<Data>(File.ReadAllText(GamePaths.GraphicsSettingsFile))
                        ?? new Data();
            }
            catch (Exception)
            {
                _data = new Data();
            }

            // Re-clamp deserialized values: the setters clamp the slider path, but a hand-edited / corrupt
            // file could otherwise feed an out-of-range value straight to a shader uniform or the radius chain.
            _data.RenderDistanceChunks = Clamp(_data.RenderDistanceChunks, MinRenderDistanceChunks, MaxRenderDistanceChunks);
            _data.Fov = Clamp(_data.Fov, MinFov, MaxFov);
            _data.MouseSensitivity = Clamp(_data.MouseSensitivity, MinMouseSensitivity, MaxMouseSensitivity);
            _data.Brightness = Clamp(_data.Brightness, MinBrightness, MaxBrightness);
            _data.LodHorizonQuality = Clamp(_data.LodHorizonQuality, MinLodHorizonQuality, MaxLodHorizonQuality);
            _data.LodHorizonChunks = Clamp(_data.LodHorizonChunks, MinLodHorizonChunks, MaxLodHorizonChunks);
        }

        private static void Save()
        {
            if (SuppressSave) return;
            try
            {
                File.WriteAllText(GamePaths.GraphicsSettingsFile,
                    JsonConvert.SerializeObject(_data, Formatting.Indented));
            }
            catch (Exception e)
            {
                Logger.Warn("Failed to save graphics settings: " + e.Message);
            }
        }

        /// <summary>Push the saved vsync preference onto the renderer's surface present mode. Called once at
        /// startup (after the surface exists) and by the <see cref="VSync"/> setter on a runtime change.</summary>
        public static void ApplyVSync() => Renderer.SetVSync(_data.VSync);

        private static void ApplyFullscreen()
        {
            if (ClientResources.Window == null) return;
            ClientResources.Window.WindowState = _data.Fullscreen ? WindowState.Fullscreen : WindowState.Normal;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    }
}

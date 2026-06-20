using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using MinecraftClone3API.IO;
using OpenTK.Windowing.Common;

namespace MinecraftClone3API.Client
{
    /// <summary>
    /// Persistent, user-tunable graphics options. <see cref="Load"/> reads the saved values at startup (no
    /// GL required), and each setter writes the file back and pushes window-level state onto the live
    /// <see cref="ClientResources.Window"/>. The shadow toggle has no window state — it is read directly by
    /// <see cref="Graphics.WorldRenderer"/> each frame to skip the shadow passes and force surfaces lit.
    /// </summary>
    public static class GraphicsSettings
    {
        private class Data
        {
            [JsonConverter(typeof(StringEnumConverter))]
            public VSyncMode VSync = VSyncMode.On;
            public bool Shadows = true;
            public bool Fullscreen = false;
        }

        private static Data _data = new Data();

        public static VSyncMode VSync
        {
            get => _data.VSync;
            set { _data.VSync = value; Save(); ApplyVSync(); }
        }

        public static bool Shadows
        {
            get => _data.Shadows;
            set { _data.Shadows = value; Save(); }
        }

        public static bool Fullscreen
        {
            get => _data.Fullscreen;
            set { _data.Fullscreen = value; Save(); ApplyFullscreen(); }
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
        }

        private static void Save() =>
            File.WriteAllText(GamePaths.GraphicsSettingsFile,
                JsonConvert.SerializeObject(_data, Formatting.Indented));

        private static void ApplyVSync()
        {
            if (ClientResources.Window != null) ClientResources.Window.VSync = _data.VSync;
        }

        private static void ApplyFullscreen()
        {
            if (ClientResources.Window == null) return;
            ClientResources.Window.WindowState = _data.Fullscreen ? WindowState.Fullscreen : WindowState.Normal;
        }
    }
}

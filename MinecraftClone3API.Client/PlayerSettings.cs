using System;
using System.IO;
using Newtonsoft.Json;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Client
{
    /// <summary>
    /// Persistent player identity. <see cref="Name"/> is sent in the multiplayer login so each player's
    /// inventory/position save under their own file server-side (an empty name would collapse every player onto
    /// one shared save). GPU-free; mirrors <see cref="GraphicsSettings"/>'s load-at-startup / save-on-set
    /// pattern. There is no in-game name field yet, so the name is set by editing <c>PlayerSettings.json</c>
    /// (default <c>"Player"</c>).
    /// </summary>
    public static class PlayerSettings
    {
        private class Data
        {
            public string Name = "Player";
        }

        private static Data _data = new Data();

        public static string Name
        {
            get => string.IsNullOrWhiteSpace(_data.Name) ? "Player" : _data.Name;
            set { _data.Name = value; Save(); }
        }

        public static void Load()
        {
            if (!File.Exists(GamePaths.PlayerSettingsFile)) return;
            try
            {
                _data = JsonConvert.DeserializeObject<Data>(File.ReadAllText(GamePaths.PlayerSettingsFile))
                        ?? new Data();
            }
            catch (Exception)
            {
                _data = new Data();
            }
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(GamePaths.PlayerSettingsFile,
                    JsonConvert.SerializeObject(_data, Formatting.Indented));
            }
            catch (Exception e)
            {
                Logger.Warn("Failed to save player settings: " + e.Message);
            }
        }
    }
}

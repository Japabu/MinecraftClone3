using System;
using System.IO;

namespace MinecraftClone3API.IO
{
    /// <summary>
    /// Central resolver for runtime file locations.
    ///
    /// Read-only content (plugins, keybindings) is shipped next to the executable and resolved
    /// against <see cref="AppContext.BaseDirectory"/>, so the game runs regardless of the current
    /// working directory. Writable data (world saves, resource settings) lives in a per-user
    /// application-data directory instead of next to the binary.
    /// </summary>
    public static class GamePaths
    {
        /// <summary>Directory containing the executable and its copied content.</summary>
        public static string ContentDir => AppContext.BaseDirectory;

        /// <summary>Directory holding the installed plugins (folders or zip archives).</summary>
        public static string PluginsDir => Path.Combine(ContentDir, "Plugins");

        /// <summary>Keybindings file shipped alongside the binary.</summary>
        public static string KeybindingsFile => Path.Combine(ContentDir, "Keybindings.txt");

        /// <summary>
        /// Per-user, writable application-data directory. Created on access.
        /// Resolves to <c>%LocalAppData%\MinecraftClone3</c> on Windows and
        /// <c>~/.local/share/MinecraftClone3</c> on Linux/macOS.
        /// </summary>
        public static string UserDataDir
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MinecraftClone3");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>Per-user resource settings file (which plugins are enabled and their order).</summary>
        public static string ResourceSettingsFile => Path.Combine(UserDataDir, "ResourceSettings.json");

        /// <summary>Per-user graphics options file (vsync, shadows, fullscreen).</summary>
        public static string GraphicsSettingsFile => Path.Combine(UserDataDir, "GraphicsSettings.json");

        /// <summary>Per-user world-save directory.</summary>
        public static string WorldDir => Path.Combine(UserDataDir, "World");

        /// <summary>World metadata file (persisted seed) inside <see cref="WorldDir"/>.</summary>
        public static string LevelFile => Path.Combine(WorldDir, "level.dat");

        /// <summary>
        /// Per-user resource-pack directory. Created on access. Packs (folders, zips, or Minecraft
        /// client jars) dropped here supply the assets the plugins reference; they cascade on top of
        /// the shipped plugins (a later-sorting pack overrides earlier sources on key collision).
        /// </summary>
        public static string ResourcePacksDir
        {
            get
            {
                var dir = Path.Combine(UserDataDir, "ResourcePacks");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }
}

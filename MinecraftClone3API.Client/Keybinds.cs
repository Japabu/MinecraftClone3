using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3API.Client
{
    /// <summary>A rebindable in-game control.</summary>
    public enum GameAction
    {
        Forward,
        Back,
        Left,
        Right,
        Jump,
        Sneak,
        Sprint,
        Drop,
        Inventory
    }

    /// <summary>
    /// Persistent, user-tunable key bindings. <see cref="Load"/> reads the saved map at startup; each
    /// <see cref="Set"/> writes the file back. The player controller / world state read the live binding each
    /// frame via <see cref="IsDown"/> / <see cref="IsPressed"/>, so a rebind takes effect immediately. The
    /// Controls screen edits these.
    /// </summary>
    public static class Keybinds
    {
        private static readonly Dictionary<GameAction, Keys> _binds = Defaults();

        private static Dictionary<GameAction, Keys> Defaults() => new Dictionary<GameAction, Keys>
        {
            {GameAction.Forward, Keys.W},
            {GameAction.Back, Keys.S},
            {GameAction.Left, Keys.A},
            {GameAction.Right, Keys.D},
            {GameAction.Jump, Keys.Space},
            {GameAction.Sneak, Keys.LeftShift},
            {GameAction.Sprint, Keys.LeftControl},
            {GameAction.Drop, Keys.Q},
            {GameAction.Inventory, Keys.E}
        };

        public static IReadOnlyList<GameAction> All { get; } =
            (GameAction[]) Enum.GetValues(typeof(GameAction));

        public static Keys Get(GameAction action) => _binds.TryGetValue(action, out var k) ? k : Keys.Unknown;

        public static void Set(GameAction action, Keys key)
        {
            _binds[action] = key;
            Save();
        }

        public static void ResetDefaults()
        {
            foreach (var pair in Defaults()) _binds[pair.Key] = pair.Value;
            Save();
        }

        public static bool IsDown(KeyboardState ks, GameAction action) => ks.IsKeyDown(Get(action));
        public static bool IsPressed(KeyboardState ks, GameAction action) => ks.IsKeyPressed(Get(action));

        /// <summary>Human-readable label for the action (e.g. "Open Inventory").</summary>
        public static string DisplayName(GameAction action)
        {
            switch (action)
            {
                case GameAction.Forward: return "Walk Forward";
                case GameAction.Back: return "Walk Backward";
                case GameAction.Left: return "Strafe Left";
                case GameAction.Right: return "Strafe Right";
                case GameAction.Jump: return "Jump / Fly Up";
                case GameAction.Sneak: return "Sneak / Fly Down";
                case GameAction.Sprint: return "Sprint";
                case GameAction.Drop: return "Drop Item";
                case GameAction.Inventory: return "Open Inventory";
                default: return action.ToString();
            }
        }

        public static void Load()
        {
            if (!File.Exists(GamePaths.KeybindsFile)) return;
            try
            {
                var saved = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText(GamePaths.KeybindsFile));
                if (saved == null) return;
                foreach (var pair in saved)
                    if (Enum.TryParse(pair.Key, out GameAction action) && Enum.TryParse(pair.Value, out Keys key))
                        _binds[action] = key;
            }
            catch (Exception)
            {
                // Corrupt/hand-edited file: keep the defaults already in _binds.
            }
        }

        private static void Save()
        {
            try
            {
                var map = new Dictionary<string, string>();
                foreach (var pair in _binds) map[pair.Key.ToString()] = pair.Value.ToString();
                File.WriteAllText(GamePaths.KeybindsFile, JsonConvert.SerializeObject(map, Formatting.Indented));
            }
            catch (Exception e)
            {
                Logger.Warn("Failed to save keybinds: " + e.Message);
            }
        }
    }
}

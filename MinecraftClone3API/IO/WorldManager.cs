using System;
using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.IO
{
    /// <summary>A singleplayer world on disk: its directory plus the metadata read from level.dat.</summary>
    public class WorldInfo
    {
        public string Directory;
        public string Name;
        public long Seed;
        public DateTime LastPlayed;
    }

    /// <summary>
    /// Lists, creates, and deletes the singleplayer worlds living under <see cref="GamePaths.WorldsDir"/>
    /// (one subfolder per world, each with a <c>level.dat</c>). The world-selection screens drive this.
    /// </summary>
    public static class WorldManager
    {
        public static List<WorldInfo> ListWorlds()
        {
            var worlds = new List<WorldInfo>();
            foreach (var dir in Directory.EnumerateDirectories(GamePaths.WorldsDir))
            {
                var meta = WorldMetadata.Load(dir);
                if (meta == null) continue;
                worlds.Add(new WorldInfo
                {
                    Directory = dir,
                    Name = meta.Name,
                    Seed = meta.Seed,
                    LastPlayed = meta.LastPlayed
                });
            }

            worlds.Sort((a, b) => b.LastPlayed.CompareTo(a.LastPlayed));
            return worlds;
        }

        public static WorldInfo CreateWorld(string displayName, long seed)
        {
            var dir = UniqueWorldDir(SanitizeFolderName(displayName));
            var meta = new WorldMetadata {Name = displayName, Seed = seed, LastPlayed = DateTime.Now};
            WorldMetadata.Save(dir, meta);
            Logger.Info($"Created world \"{displayName}\" ({dir}) with seed {seed}");
            return new WorldInfo {Directory = dir, Name = displayName, Seed = seed, LastPlayed = meta.LastPlayed};
        }

        public static void DeleteWorld(WorldInfo info)
        {
            if (Directory.Exists(info.Directory))
                Directory.Delete(info.Directory, true);
        }

        public static void MarkPlayed(WorldInfo info)
        {
            var meta = WorldMetadata.Load(info.Directory);
            if (meta == null) return;
            meta.LastPlayed = DateTime.Now;
            WorldMetadata.Save(info.Directory, meta);
            info.LastPlayed = meta.LastPlayed;
        }

        private static string UniqueWorldDir(string folderName)
        {
            var baseDir = Path.Combine(GamePaths.WorldsDir, folderName);
            var dir = baseDir;
            var suffix = 1;
            while (Directory.Exists(dir))
                dir = $"{baseDir} ({suffix++})";
            return dir;
        }

        private static string SanitizeFolderName(string name)
        {
            var chars = name.ToCharArray();
            var invalid = Path.GetInvalidFileNameChars();
            for (var i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';

            var sanitized = new string(chars).Trim();
            return sanitized.Length == 0 ? "World" : sanitized;
        }
    }
}

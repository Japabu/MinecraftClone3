using System;
using System.IO;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Per-world metadata persisted to <c>&lt;worldDir&gt;/level.dat</c>: the display name, the generation
    /// seed (so a reload reproduces the same terrain), and when the world was last played (so a world list
    /// can sort most-recent-first). A world directory is identified solely by this file.
    /// </summary>
    public class WorldMetadata
    {
        private const int Version = 2;
        public const string LevelFileName = "level.dat";

        public string Name;
        public long Seed;
        public DateTime LastPlayed;

        public static string LevelFilePath(string worldDir) => Path.Combine(worldDir, LevelFileName);

        public static WorldMetadata Load(string worldDir)
        {
            var file = LevelFilePath(worldDir);
            if (!File.Exists(file)) return null;

            try
            {
                using (var reader = new BinaryReader(File.OpenRead(file)))
                {
                    reader.ReadInt32();
                    var name = reader.ReadString();
                    var seed = reader.ReadInt64();
                    var lastPlayedTicks = reader.ReadInt64();
                    return new WorldMetadata {Name = name, Seed = seed, LastPlayed = new DateTime(lastPlayedTicks)};
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not read \"{file}\"");
                Logger.Exception(e);
                return null;
            }
        }

        public static void Save(string worldDir, WorldMetadata meta)
        {
            Directory.CreateDirectory(worldDir);
            using (var writer = new BinaryWriter(File.Create(LevelFilePath(worldDir))))
            {
                writer.Write(Version);
                writer.Write(meta.Name);
                writer.Write(meta.Seed);
                writer.Write(meta.LastPlayed.Ticks);
            }
        }

        /// <summary>Loads the world's metadata, or mints + saves a fresh one (random seed unless given).
        /// The dedicated server uses this against its fixed <see cref="IO.GamePaths.WorldDir"/>.</summary>
        public static WorldMetadata LoadOrCreate(string worldDir, string name, long? seed = null)
        {
            var existing = Load(worldDir);
            if (existing != null) return existing;

            var meta = new WorldMetadata
            {
                Name = name,
                Seed = seed ?? Random.Shared.NextInt64(),
                LastPlayed = DateTime.Now
            };
            Save(worldDir, meta);
            Logger.Info($"Created new world \"{meta.Name}\" with seed {meta.Seed}");
            return meta;
        }
    }
}

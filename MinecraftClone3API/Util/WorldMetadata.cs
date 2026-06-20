using System;
using System.IO;
using MinecraftClone3API.IO;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Per-world metadata persisted to <see cref="GamePaths.LevelFile"/>. Currently just the generation
    /// seed: read it if the world exists, otherwise mint a random one and write it so a reload reproduces
    /// the same terrain. The client and dedicated server both resolve the same <see cref="GamePaths.WorldDir"/>,
    /// so they agree on the seed automatically.
    /// </summary>
    public static class WorldMetadata
    {
        private const int Version = 1;

        public static long LoadOrCreateSeed()
        {
            var file = GamePaths.LevelFile;
            if (File.Exists(file))
            {
                try
                {
                    using (var reader = new BinaryReader(File.OpenRead(file)))
                    {
                        reader.ReadInt32();
                        return reader.ReadInt64();
                    }
                }
                catch (Exception e)
                {
                    Logger.Warn($"Could not read \"{file}\", generating a fresh seed");
                    Logger.Exception(e);
                }
            }

            var seed = Random.Shared.NextInt64();
            Directory.CreateDirectory(GamePaths.WorldDir);
            using (var writer = new BinaryWriter(File.Create(file)))
            {
                writer.Write(Version);
                writer.Write(seed);
            }

            Logger.Info($"Created new world with seed {seed}");
            return seed;
        }
    }
}

using System;
using System.IO;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.IO
{
    /// <summary>Per-player save: the inventory (hotbar + main grid + selected slot), survival stats
    /// (health/hunger/saturation/exhaustion/air/game mode), and the last position + look (with the dimension it
    /// was in), for one player name, stored at <c>&lt;worldDir&gt;/Players/&lt;name&gt;.dat</c> under the primary
    /// world dir. Server-side only; the client never touches it.</summary>
    public static class PlayerSerializer
    {
        private const string PlayersFolder = "Players";
        private const int Version = 2;

        private static string FileFor(string worldDir, string name)
        {
            var safe = string.IsNullOrEmpty(name) ? "player" : name;
            foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(worldDir, PlayersFolder, safe + ".dat");
        }

        /// <summary>Loads a player's inventory, stats, and saved position/look, and reports the dimension they
        /// logged off in via <paramref name="savedDimensionKey"/> so the caller can transfer them back there.
        /// Returns false if there is no save yet (caller seeds defaults); a corrupt file is treated as "no save"
        /// rather than crashing. On a false return <paramref name="savedDimensionKey"/> is null.</summary>
        public static bool Load(string worldDir, string name, Inventory inventory, EntityPlayer player,
            out string savedDimensionKey)
        {
            savedDimensionKey = null;
            var file = FileFor(worldDir, name);
            if (!File.Exists(file)) return false;

            try
            {
                using (var reader = new BinaryReader(File.OpenRead(file)))
                {
                    var version = reader.ReadInt32();
                    if (version != Version)
                    {
                        Logger.Warn($"Player \"{name}\" is save version {version}, expected {Version} — resetting");
                        return false;
                    }

                    inventory.Read(reader);
                    player.Health = reader.ReadSingle();
                    player.Hunger = reader.ReadSingle();
                    player.Saturation = reader.ReadSingle();
                    player.Exhaustion = reader.ReadSingle();
                    player.Air = reader.ReadInt32();
                    player.GameMode = (GameMode) reader.ReadByte();

                    savedDimensionKey = reader.ReadString();
                    var position = new Vector3D<float>(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    player.Position = position;
                    player.LastTickPosition = position;
                    player.Pitch = reader.ReadSingle();
                    player.Yaw = reader.ReadSingle();
                }

                return true;
            }
            catch (Exception e)
            {
                Logger.Error($"Could not load player \"{name}\": {e.Message} — resetting inventory");
                return false;
            }
        }

        public static void Save(string worldDir, string name, Inventory inventory, EntityPlayer player,
            string dimensionKey)
        {
            var file = new FileInfo(FileFor(worldDir, name));
            file.Directory.Create();

            var tmp = file.FullName + ".tmp";
            using (var writer = new BinaryWriter(File.Create(tmp)))
            {
                writer.Write(Version);
                inventory.Write(writer);
                writer.Write(player.Health);
                writer.Write(player.Hunger);
                writer.Write(player.Saturation);
                writer.Write(player.Exhaustion);
                writer.Write(player.Air);
                writer.Write((byte) player.GameMode);
                writer.Write(dimensionKey);
                writer.Write(player.Position.X);
                writer.Write(player.Position.Y);
                writer.Write(player.Position.Z);
                writer.Write(player.Pitch);
                writer.Write(player.Yaw);
            }
            File.Move(tmp, file.FullName, true);
        }
    }
}

using System;
using System.IO;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.IO
{
    /// <summary>Per-player save: the inventory (hotbar + main grid + selected slot) plus the survival stats
    /// (health/hunger/saturation/exhaustion/air/game mode) for one player name, stored at
    /// <c>&lt;worldDir&gt;/Players/&lt;name&gt;.dat</c>. Server-side only; the client never touches it.</summary>
    public static class PlayerSerializer
    {
        private const string PlayersFolder = "Players";

        private static string FileFor(string worldDir, string name)
        {
            var safe = string.IsNullOrEmpty(name) ? "player" : name;
            foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(worldDir, PlayersFolder, safe + ".dat");
        }

        /// <summary>Loads a player's inventory and stats, or returns false if there is no save yet (caller seeds
        /// defaults). A corrupt file is treated as "no save" rather than crashing.</summary>
        public static bool Load(string worldDir, string name, Inventory inventory, EntityPlayer player)
        {
            var file = FileFor(worldDir, name);
            if (!File.Exists(file)) return false;

            try
            {
                using (var reader = new BinaryReader(File.OpenRead(file)))
                {
                    inventory.Read(reader);
                    player.Health = reader.ReadSingle();
                    player.Hunger = reader.ReadSingle();
                    player.Saturation = reader.ReadSingle();
                    player.Exhaustion = reader.ReadSingle();
                    player.Air = reader.ReadInt32();
                    player.GameMode = (GameMode) reader.ReadByte();
                }

                return true;
            }
            catch (Exception e)
            {
                Logger.Error($"Could not load player \"{name}\": {e.Message} — resetting inventory");
                return false;
            }
        }

        public static void Save(string worldDir, string name, Inventory inventory, EntityPlayer player)
        {
            var file = new FileInfo(FileFor(worldDir, name));
            file.Directory.Create();

            using (var writer = new BinaryWriter(file.Create()))
            {
                inventory.Write(writer);
                writer.Write(player.Health);
                writer.Write(player.Hunger);
                writer.Write(player.Saturation);
                writer.Write(player.Exhaustion);
                writer.Write(player.Air);
                writer.Write((byte) player.GameMode);
            }
        }
    }
}

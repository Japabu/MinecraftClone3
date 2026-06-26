using System;
using System.IO;
using System.Threading;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Networking;
using MinecraftClone3API.Plugins;
using MinecraftClone3API.Util;

namespace MinecraftClone3Server
{
    internal static class Program
    {
        private const int TickRateHz = 20;

        private static void Main()
        {
            LoadPlugins();

            var meta = WorldMetadata.LoadOrCreate(GamePaths.WorldDir, "world");
            var world = new WorldServer(meta.Seed, GamePaths.WorldDir);
            var network = new ServerNetwork(world);
            network.Listen(ServerNetwork.DefaultPort);

            var running = true;
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                running = false;
            };

            Logger.Info("Server started. Press Ctrl-C to stop.");

            var tickInterval = TimeSpan.FromSeconds(1.0 / TickRateHz);
            while (running)
            {
                var start = DateTime.Now;

                network.TickWorlds();
                network.Pump();

                var sleep = tickInterval - (DateTime.Now - start);
                if (sleep > TimeSpan.Zero) Thread.Sleep(sleep);
            }

            Logger.Info("Stopping server...");
            network.Stop();
            network.UnloadWorlds();
        }

        /// <summary>
        /// Loads plugins exactly like the client's resource-loading screen but without any GL calls
        /// (no client resources, no texture upload) — the server only needs the block registry.
        /// </summary>
        private static void LoadPlugins()
        {
            CommonResources.Load();
            PluginManager.AddPlugin(new FileSystemRaw(new DirectoryInfo(Path.Combine(GamePaths.PluginsDir, "System"))));

            var pluginsDir = new DirectoryInfo(GamePaths.PluginsDir);
            foreach (var dir in pluginsDir.EnumerateDirectories())
                PluginManager.AddPlugin(new FileSystemRaw(dir));
            foreach (var file in pluginsDir.EnumerateFiles())
                PluginManager.AddPlugin(new FileSystemCompressed(file));

            PluginManager.AddResourcePacks();

            PluginManager.LoadResources((total, state, plugin) => Logger.Debug($"{state} {plugin}"));
            PluginManager.LoadPlugins((total, state, plugin) => Logger.Debug($"{state} {plugin}"));
        }
    }
}

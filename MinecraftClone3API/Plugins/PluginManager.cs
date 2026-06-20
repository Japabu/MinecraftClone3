using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MinecraftClone3API.Plugins
{
    public static class PluginManager
    {
        private const string PluginInfoFile = "PluginInfo.json";

        private static readonly Dictionary<string, PluginContext> PluginDlls = new Dictionary<string, PluginContext>();
        private static readonly List<string> LoadedPlugins = new List<string>();

        public static void LoadResources(Action<float, string, string> progress)
        {
            I18N.Load(t => progress(t * 0.5f, "Loading", "Translations"));

            progress(0.5f, "system.loading.resources.clearCache", "");
            ResourceReader.ClearCache();

            var part = 1f / PluginDlls.Count;
            var total = 0f;
            foreach (var plugin in PluginDlls)
            {
                progress(0.5f + total * 0.5f, "system.loading.resources.plugin", plugin.Value.PluginAttribute.Name);
                total += part;
                plugin.Value.Plugin.LoadResources(plugin.Value);
            }
        }

        public static void LoadPlugins(Action<float, string, string> progress)
        {
            var part = 0.3333333F / PluginDlls.Count;
            var total = 0f;
            foreach (var plugin in PluginDlls)
            {
                progress(total, "system.loading.preLoad", plugin.Value.PluginAttribute.Name);
                total += part;
                plugin.Value.Plugin.PreLoad(plugin.Value);
            }

            foreach (var plugin in PluginDlls)
            {
                progress(total, "system.loading.load", plugin.Value.PluginAttribute.Name);
                total += part;
                plugin.Value.Plugin.Load(plugin.Value);
            }

            foreach (var plugin in PluginDlls)
            {
                progress(total, "system.loading.postLoad", plugin.Value.PluginAttribute.Name);
                total += part;
                plugin.Value.Plugin.PostLoad(plugin.Value);
            }
        }

        private const string ResourcePacksReadme =
            "Drop resource packs here.\n\n" +
            "Each pack may be a folder, a .zip, or a Minecraft client .jar. The Vanilla plugin\n" +
            "references the real Minecraft resource paths (block/stone, block/grass_block, ...),\n" +
            "so a 1.13+ client jar dropped in this folder supplies its models and textures.\n\n" +
            "Packs cascade in name order; a later-sorting pack overrides earlier sources.\n";

        /// <summary>
        /// Adds a resource pack: indexes its assets and language files into the cascade but does not
        /// require a <c>PluginInfo.json</c> or load any DLL, so a plain Minecraft client jar can be
        /// dropped in as a pack without logging the "no info file" error.
        /// </summary>
        public static void AddResourcePack(FileSystem fileSystem)
        {
            ResourceManager.AddFileSystem(fileSystem, fileSystem.GetFiles());
        }

        /// <summary>
        /// Scans <see cref="GamePaths.ResourcePacksDir"/> and indexes every pack (folders,
        /// <c>.zip</c>/<c>.jar</c> archives) in name order, so a later-sorting pack cascades over
        /// earlier sources. Called after the plugins are added, so packs override plugin assets.
        /// </summary>
        public static void AddResourcePacks()
        {
            var dir = new DirectoryInfo(GamePaths.ResourcePacksDir);

            var packs = new List<(string Name, Func<FileSystem> Create)>();
            foreach (var sub in dir.EnumerateDirectories())
            {
                var captured = sub;
                packs.Add((captured.Name, () => new FileSystemRaw(captured)));
            }
            foreach (var file in dir.EnumerateFiles())
            {
                var ext = file.Extension.ToLowerInvariant();
                if (ext != ".zip" && ext != ".jar") continue;
                var captured = file;
                packs.Add((captured.Name, () => new FileSystemCompressed(captured)));
            }

            if (packs.Count == 0)
            {
                var readme = Path.Combine(dir.FullName, "README.txt");
                if (!File.Exists(readme)) File.WriteAllText(readme, ResourcePacksReadme);
                Logger.Warn($"No resource packs found in \"{dir.FullName}\" — blocks will render with " +
                            "placeholder textures. Drop a Minecraft client jar (or any resource pack) there.");
                return;
            }

            packs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var pack in packs)
            {
                try
                {
                    AddResourcePack(pack.Create());
                    Logger.Info($"Resource pack \"{pack.Name}\" added");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to load resource pack \"{pack.Name}\"");
                    Logger.Exception(ex);
                }
            }
        }

        public static void AddPlugin(FileSystem fileSystem)
        {
            var pluginFiles = fileSystem.GetFiles();
            ResourceManager.AddFileSystem(fileSystem, pluginFiles);
            if (!pluginFiles.Contains(PluginInfoFile))
            {
                Logger.Error($"Plugin \"{fileSystem.Name}\" does not have an info file and was ignored!");
                return;
            }

            var pluginInfoFileData = fileSystem.ReadFile(PluginInfoFile);
            var pluginInfo = JsonConvert.DeserializeObject<PluginInfo>(Encoding.UTF8.GetString(pluginInfoFileData));

            if (LoadedPlugins.Contains(pluginInfo.PluginName)) return;
            LoadedPlugins.Add(pluginInfo.PluginName);

            if (pluginInfo.PluginDlls == null) return;
            foreach (var dllPath in pluginInfo.PluginDlls)
            {
                if (!pluginFiles.Contains(dllPath))
                {
                    Logger.Error($"Plugin dll \"{dllPath}\" from \"{pluginInfo.PluginName}\" not found!");
                    continue;
                }

                try
                {
                    var dllData = fileSystem.ReadFile(dllPath);
                    var assembly = Assembly.Load(dllData);
                    foreach (var type in assembly.GetTypes().Where(t => typeof(IPlugin).IsAssignableFrom(t)))
                    {
                        var attributes = type.GetCustomAttributes(typeof(PluginAttribute), false);
                        if (attributes.Length != 1) continue;

                        var plugin = (IPlugin) Activator.CreateInstance(type);
                        var attribute = (PluginAttribute) attributes[0];
                        var pluginContext = new PluginContext(attribute, plugin);
                        PluginDlls.Add(attribute.Id, pluginContext);

                        Logger.Info($"Plugin dll \"{attribute.Id}\" added");
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Logger.Error($"There is a problem with plugin dll \"{dllPath}\" in \"{pluginInfo.PluginName}\":");
                    Logger.Exception(ex.LoaderExceptions[0]);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error loading plugin dll:\"{dllPath}\" in \"{pluginInfo.PluginName}\"");
                    Logger.Exception(ex);
                }
            }
        }
    }
}
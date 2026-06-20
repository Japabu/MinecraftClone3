using System.IO;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Plugins;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;

namespace MinecraftClone3.States
{
    internal class GuiResourceLoading : GuiBase
    {
        private static Texture _background;
        private static Texture _progressBar;
        private static Texture _progressBarFull;

        private bool _done;
        private int _progress;
        private string _text;

        private readonly GameWindow _window;

        public GuiResourceLoading(GameWindow window)
        {
            _window = window;

            CommonResources.Load();
            PluginManager.AddPlugin(new FileSystemRaw(new DirectoryInfo(Path.Combine(GamePaths.PluginsDir, "System"))));
            ResourceReader.ClearCache();
            ClientResources.Load(window);
            BoundingBoxRenderer.Load();
            EntityRenderer.Load();
            ChunkBorderRenderer.Load();

            _background = ResourceReader.ReadTexture("System/Textures/Gui/ResourceLoadingBackground.png");
            _progressBar = ResourceReader.ReadTexture("System/Textures/Gui/Progressbar.png");
            _progressBarFull = ResourceReader.ReadTexture("System/Textures/Gui/ProgressbarFull.png");

            Start(false);
        }

        public GuiResourceLoading()
        {
            Start(true);
        }

        public override void Update(bool focused)
        {
            if (!_done) return;
            StateEngine.ReplaceState(new GuiMainMenu(_window));
        }

        public override void Render()
        {
            GuiRenderer.DrawTexture(_background, new Rectangle(0, 0, 960, 540), null);
            GuiRenderer.DrawTexture(_progressBar, new Rectangle(100, 340, (int)ScaledResolution.GuiResolution.X - 100, 420), null);
            GuiRenderer.DrawTexture(_progressBarFull, Rectangle.FromSize(100, 340, 800 / 100 * _progress, 80), null);

            //GuiRenderer.DrawTexture(background, new Vector4(-1,-1,1,1), new Vector4(0,0,1,1));
        }

        private void Start(bool reload)
        {
            // The OpenTK 2 build loaded resources on a second thread with its own shared GL
            // context. macOS/NSGL does not support creating a second shared context this way
            // (GLFW also requires windows on the main thread), so resources are uploaded
            // synchronously on the main thread, which already owns the GL context.
            Work(reload);
            _done = true;
        }

        private void Work(bool reload)
        {
            if (!reload)
            {
                //Add plugins in "Plugins" dir
                var pluginsDir = new DirectoryInfo(GamePaths.PluginsDir);
                foreach (var dir in pluginsDir.EnumerateDirectories())
                    PluginManager.AddPlugin(new FileSystemRaw(dir));
                foreach (var file in pluginsDir.EnumerateFiles())
                    PluginManager.AddPlugin(new FileSystemCompressed(file));

                //Cascade user resource packs on top of the plugins
                PluginManager.AddResourcePacks();
            }

            //Load resources
            PluginManager.LoadResources(
                (total, state, plugin) =>
                {
                    _progress = (int) (total * 50);
                    _text = $"{I18N.Get(state)} {plugin} ({_progress}%)";
                    Logger.Debug($"{I18N.GetOrdinal(state)} {plugin} ({_progress}%)");
                });

            if (!reload)
            {
                //Load plugins
                PluginManager.LoadPlugins(
                    (total, state, plugin) =>
                    {
                        _progress = (int) (total * 50) + 50;
                        _text = $"{I18N.Get(state)} {plugin} ({_progress}%)";
                        Logger.Debug($"{I18N.GetOrdinal(state)} {plugin} ({_progress}%)");
                    });
            }

            // Blocks load their models/textures in their constructors, which run while plugins
            // register them in LoadPlugins (Load). The GPU texture arrays must therefore be built
            // afterwards, otherwise they would be uploaded empty and every block samples as black.
            _progress = 100;
            _text = $"{I18N.Get("system.loading.resources.uploadTextures")} ({_progress}%)";
            Logger.Debug($"{I18N.GetOrdinal("system.loading.resources.uploadTextures")} ({_progress}%)");
            BlockTextureManager.Upload();

            // The font comes from the Minecraft resource pack (minecraft/font/default.json), which is only
            // indexed by AddResourcePacks above — so the font must load here, after the pack, not in the
            // earlier ClientResources.Load (which runs before the pack is added).
            Font.Load();

            // The sky's sun/moon textures (minecraft/textures/environment/) are likewise pack-sourced, so they
            // load here too; absent a pack the composition shader falls back to procedural discs.
            WorldRenderer.LoadSkyTextures();
        }
    }
}
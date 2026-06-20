using System;
using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3API.Client
{
    public static class ClientResources
    {
        private const string PluginDir = "System/";

        public static GameWindow Window;

        public static GeometryFramebuffer GeometryFramebuffer;
        public static TextureFramebuffer LightFramebuffer;
        public static ShadowFramebuffer ShadowFramebuffer;
        // Half-resolution resolved sun shadow (the shadow PCF runs into this, then composition upsamples).
        public static TextureFramebuffer ShadowResolveFramebuffer;

        public static Shader WorldGeometryShader;
        public static Shader CompositionShader;
        public static Shader PointLightShader;
        public static Shader BlockOutlineShader;
        public static Shader SpriteShader;
        public static Shader ShadowDepthShader;
        public static Shader ShadowResolveShader;

        public static SpriteVertexArrayObject ScreenRectVao;

        public static Texture LoadingTexture;
        public static Texture WhitePixel;

        public static BlockModel MissingModel;
        public static BlockTexture MissingTexture;

        public static readonly Dictionary<Keys, string> Keybindings = new Dictionary<Keys, string>();

        public static void Load(GameWindow window)
        {
            Window = window;

            ResizeFrameBuffers();
            Window.Resize += args => ResizeFrameBuffers();

            WorldGeometryShader = ResourceReader.ReadShader(PluginDir + "Shaders/WorldGeometry");
            CompositionShader = ResourceReader.ReadShader(PluginDir + "Shaders/Composition");
            PointLightShader = ResourceReader.ReadShader(PluginDir + "Shaders/PointLight");
            BlockOutlineShader = ResourceReader.ReadShader(PluginDir + "Shaders/BlockOutline");
            SpriteShader = ResourceReader.ReadShader(PluginDir + "Shaders/Sprite");
            ShadowDepthShader = ResourceReader.ReadShader(PluginDir + "Shaders/ShadowDepth");
            ShadowResolveShader = ResourceReader.ReadShader(PluginDir + "Shaders/ShadowResolve");

            // Fixed-size shadow map (not window-sized), so created once here rather than in ResizeFrameBuffers.
            ShadowFramebuffer = new ShadowFramebuffer(ShadowFramebuffer.ShadowMapSize);

            ScreenRectVao = new SpriteVertexArrayObject();
            ScreenRectVao.Add(new Vector2(-1, +1), Vector2.Zero, Vector3.Zero);
            ScreenRectVao.Add(new Vector2(+1, +1), Vector2.Zero, Vector3.Zero);
            ScreenRectVao.Add(new Vector2(-1, -1), Vector2.Zero, Vector3.Zero);
            ScreenRectVao.Add(new Vector2(+1, -1), Vector2.Zero, Vector3.Zero);
            ScreenRectVao.AddFace(new uint[] {0, 2, 1, 1, 2, 3});
            ScreenRectVao.Upload();

            Samplers.Load();

            WhitePixel = new Texture(new TextureData(new byte[] {255, 255, 255, 255}, 1, 1));

            MissingModel = ResourceReader.ReadBlockModel("System/Models/MissingModel.json");
            MissingTexture = ResourceReader.ReadBlockTexture("System/Textures/Blocks/MissingTexture.png");

            //TODO: Remove

            if (File.Exists(GamePaths.KeybindingsFile))
            {
                var lines = File.ReadAllLines(GamePaths.KeybindingsFile);
                foreach (var line in lines)
                {
                    var splits = line.Split('=');

                    if (splits.Length != 2) continue;

                    if (Enum.TryParse(splits[0], true, out Keys key))
                    {
                        Keybindings.Add(key, splits[1]);
                    }
                }
            }
            else
            {
                Logger.Error($"Keybindings file \"{GamePaths.KeybindingsFile}\" was not found!");
            }
        }

        private static void ResizeFrameBuffers()
        {
            GeometryFramebuffer?.Dispose();
            GeometryFramebuffer = new GeometryFramebuffer(Window.FramebufferSize.X, Window.FramebufferSize.Y);

            LightFramebuffer?.Dispose();
            LightFramebuffer = new TextureFramebuffer(Window.FramebufferSize.X, Window.FramebufferSize.Y, false);

            // Half the framebuffer resolution (rounded up): the resolved sun shadow runs the shadow PCF at
            // quarter the pixel count, then the composition pass depth-aware-upsamples it back to full res.
            ShadowResolveFramebuffer?.Dispose();
            ShadowResolveFramebuffer = new TextureFramebuffer(
                Math.Max(1, (Window.FramebufferSize.X + 1) / 2),
                Math.Max(1, (Window.FramebufferSize.Y + 1) / 2), false);
        }
    }
}

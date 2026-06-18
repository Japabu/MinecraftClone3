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

        public static Shader WorldGeometryShader;
        public static Shader CompositionShader;
        public static Shader PointLightShader;
        public static Shader BlockOutlineShader;
        public static Shader SpriteShader;

        public static SpriteVertexArrayObject ScreenRectVao;

        public static Texture LoadingTexture;

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

            ScreenRectVao = new SpriteVertexArrayObject();
            ScreenRectVao.Add(new Vector2(-1, +1), Vector2.Zero, Vector3.Zero);
            ScreenRectVao.Add(new Vector2(+1, +1), Vector2.Zero, Vector3.Zero);
            ScreenRectVao.Add(new Vector2(-1, -1), Vector2.Zero, Vector3.Zero);
            ScreenRectVao.Add(new Vector2(+1, -1), Vector2.Zero, Vector3.Zero);
            ScreenRectVao.AddFace(new uint[] {0, 2, 1, 1, 2, 3});
            ScreenRectVao.Upload();

            Samplers.Load();

            MissingModel = ResourceReader.ReadBlockModel("System/Models/MissingModel.json");
            MissingTexture = ResourceReader.ReadBlockTexture("System/Textures/Blocks/MissingTexture.png");

            //TODO: Remove

            var lines = File.ReadAllLines("Keybindings.txt");
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

        private static void ResizeFrameBuffers()
        {
            GeometryFramebuffer?.Dispose();
            GeometryFramebuffer = new GeometryFramebuffer(Window.FramebufferSize.X, Window.FramebufferSize.Y);

            LightFramebuffer?.Dispose();
            LightFramebuffer = new TextureFramebuffer(Window.FramebufferSize.X, Window.FramebufferSize.Y, false);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.Input;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Silk.NET.Windowing;

namespace MinecraftClone3API.Client
{
    /// <summary>
    /// The client's window + input + shared-render-resource hub. Holds the Silk.NET window and the
    /// <see cref="InputManager"/> (the engine reads input from <c>ClientResources.Input</c> rather than
    /// threading it through state methods), the deferred <see cref="GBufferTargets"/>, and the small shared
    /// textures. The renderer's pipelines/HDR target live in <see cref="Renderer"/>; per-world targets
    /// (shadow map/resolve) live in <see cref="WorldRenderer"/>.
    /// </summary>
    public static class ClientResources
    {
        private const string PluginDir = "System/";

        public static IWindow Window;
        public static InputManager Input;

        public static GBufferTargets GBuffer;

        public static Texture WhitePixel;

        public static int Width => Window.FramebufferSize.X;
        public static int Height => Window.FramebufferSize.Y;

        public static void Load(IWindow window, InputManager input)
        {
            Window = window;
            Input = input;

            Renderer.Load(Width, Height, ResourceReader.ReadString);
            GBuffer = new GBufferTargets(Width, Height);
            ScaledResolution.Update();

            Window.FramebufferResize += OnFramebufferResize;

            WhitePixel = new Texture(new TextureData(new byte[] { 255, 255, 255, 255 }, 1, 1));

            CommonResources.MissingModel = ResourceReader.ReadBlockModel("System/Models/MissingModel.json");
            CommonResources.MissingTexture = ResourceReader.ReadBlockTexture("System/Textures/Blocks/MissingTexture.png");
        }

        private static void OnFramebufferResize(Vector2i size)
        {
            Renderer.Resize(size.X, size.Y);
            GBuffer?.Resize(size.X, size.Y);
            ScaledResolution.Update();
        }
    }
}

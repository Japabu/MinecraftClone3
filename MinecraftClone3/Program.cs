using System;
using System.Globalization;
using MinecraftClone3.States;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Entities;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace MinecraftClone3
{
    internal class GameClient : GameWindow
    {
        public GameClient(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
            : base(gameWindowSettings, nativeWindowSettings)
        {
        }

        protected override void OnLoad()
        {
            base.OnLoad();

            CursorState = CursorState.Hidden;

            // GL resources can only be created once the context exists, so the first
            // state is added here rather than before Run() (unlike the old OpenTK 2 flow).
            StateEngine.AddState(new GuiResourceLoading(this));
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            // Use the framebuffer size, not ClientSize: on HiDPI/Retina displays the
            // framebuffer is larger (e.g. 2x) than the logical client size, and a viewport
            // sized to ClientSize would only cover one corner of the window.
            GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
            ScaledResolution.Update();
        }

        protected override void OnFocusedChanged(FocusedChangedEventArgs e)
        {
            base.OnFocusedChanged(e);
            if (IsFocused)
                PlayerController.ResetMouse();
        }

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            base.OnUpdateFrame(e);
            StateEngine.Update();
        }

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            base.OnRenderFrame(e);
            StateEngine.Render();
            SwapBuffers();
        }

        protected override void OnUnload()
        {
            StateEngine.Exit();
            base.OnUnload();
        }
    }

    internal class Program
    {
        public static GameClient Window;

        private static void Main(string[] args)
        {
            //Make exceptions be english (wtf microsoft???)
            System.Threading.Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            var nativeWindowSettings = new NativeWindowSettings
            {
                ClientSize = new Vector2i(1280, 720),
                Title = "MinecraftClone3",
                Profile = ContextProfile.Core,
                // macOS only exposes OpenGL up to 4.1 Core.
                APIVersion = new Version(4, 1),
                Vsync = VSyncMode.On
            };

            var gameWindowSettings = new GameWindowSettings
            {
                UpdateFrequency = 120
            };

            Window = new GameClient(gameWindowSettings, nativeWindowSettings);
            Window.Run();
        }
    }
}

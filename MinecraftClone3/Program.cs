using System;
using System.Diagnostics;
using System.Globalization;
using MinecraftClone3.States;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.Input;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Graphics.Rhi;
using MinecraftClone3API.Util;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace MinecraftClone3
{
    /// <summary>
    /// The client entry point. Creates the Silk.NET window, brings up the WebGPU device + surface
    /// (<see cref="GpuContext"/>) and the event-driven <see cref="InputManager"/> once the window exists, and
    /// drives the fixed update / per-frame render loop through the <see cref="StateEngine"/>. The frame
    /// lifecycle itself (swapchain acquire, HDR scene target, tonemap, present) lives in <see cref="Renderer"/>;
    /// this file only opens and closes each frame around <see cref="StateEngine.Render"/>.
    /// </summary>
    internal static class Program
    {
        private static IWindow _window;
        private static InputManager _input;

        /// <summary>The live window. Exposed for the few places that need window-level state (closing the game).</summary>
        public static IWindow Window => _window;

        private static readonly Stopwatch _workTimer = new Stopwatch();
        private static readonly Stopwatch _presentTimer = new Stopwatch();
        private static double _lastUpdateMs;
        private static int _updateCalls;

        private static void Main(string[] args)
        {
            //Make exceptions be english (wtf microsoft???)
            System.Threading.Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            // Saved graphics options seed the window so it opens with the user's fullscreen choice; vsync is the
            // wgpu surface present mode, pushed onto the renderer once the surface exists (see OnLoad).
            GraphicsSettings.Load();
            Keybinds.Load();
            PlayerSettings.Load();

            // Automated modes: --benchmark boots into the FPS flythrough; --inspect boots into the LOD A/B
            // capture tool (its own large window so artifacts are visible). Both force VSync off and apply
            // deterministic settings in-memory (no save).
            Benchmark.Configure(args);
            Inspect.Configure(args);
            if (Inspect.Enabled) Inspect.ApplySettings();
            else if (Benchmark.Enabled) Benchmark.ApplySettings();

            var size = Inspect.Enabled
                ? new Vector2D<int>(Inspect.Width, Inspect.Height)
                : new Vector2D<int>(1280, 720);

            var options = WindowOptions.Default with
            {
                Size = size,
                Title = "MinecraftClone3",
                // WebGPU owns its own surface + swapchain, so Silk must NOT create an OpenGL context; present
                // pacing (vsync) is the wgpu surface present mode, set via Renderer.SetVSync, not the window.
                API = GraphicsAPI.None,
                VSync = false,
                WindowState = GraphicsSettings.Fullscreen ? WindowState.Fullscreen : WindowState.Normal,
            };

            _window = Silk.NET.Windowing.Window.Create(options);
            _window.Load += OnLoad;
            _window.Update += OnUpdate;
            _window.Render += OnRender;
            _window.Closing += OnClosing;

            _window.Run();
            _window.Dispose();
        }

        private static void OnLoad()
        {
            // The device + surface can only be created once the native window exists, so the GPU bring-up and
            // the first state (whose constructor runs ClientResources.Load → Renderer.Load) happen here.
            Gpu.Init(new GpuContext(_window));

            _input = new InputManager(_window.CreateInput());
            ClientResources.Window = _window;
            ClientResources.Input = _input;
            StateEngine.AttachInput(_input);

            // The loading + menu screens use the system cursor; a world grabs it (CursorMode.Raw) on open.
            _input.CursorMode = CursorMode.Normal;

            StateEngine.AddState(new GuiResourceLoading(false));

            // The renderer's surface is configured now (the line above ran Renderer.Load); push the saved vsync
            // preference onto it, unless an automated mode forced it off.
            if (Benchmark.Enabled || Inspect.Enabled) Renderer.SetVSync(VSyncMode.Off);
            else GraphicsSettings.ApplyVSync();
        }

        private static void OnUpdate(double dt)
        {
            _updateCalls++;
            _workTimer.Restart();
            StateEngine.Update();
            _lastUpdateMs = _workTimer.Elapsed.TotalMilliseconds;
        }

        private static void OnRender(double dt)
        {
            // BeginFrame fails to acquire the swapchain during a resize/minimize — skip the frame rather than
            // record into a dead encoder.
            if (!Renderer.BeginFrame()) return;

            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            _workTimer.Restart();
            StateEngine.Render();
            var renderMs = _workTimer.Elapsed.TotalMilliseconds;
            var renderAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            _presentTimer.Restart();
            Renderer.EndFrame();
            var presentMs = _presentTimer.Elapsed.TotalMilliseconds;

            // dt is the wall-clock interval since the last render frame (catches real fps drops); frame time is
            // EMA-smoothed so the displayed FPS doesn't jitter. GPU time is filled in by GpuTimers (M7).
            RenderDebug.FrameMs = RenderDebug.FrameMs <= 0 ? dt * 1000 : RenderDebug.FrameMs * 0.92 + dt * 1000 * 0.08;
            RenderDebug.UpdateMs = _lastUpdateMs;
            RenderDebug.GpuMs = 0;

            Profiler.Record(dt, _lastUpdateMs, renderMs, presentMs, 0, RenderDebug.GpuMs,
                _updateCalls, renderAlloc, ClientProfiling.SampleFrame());
            _updateCalls = 0;

            var fbW = _window.FramebufferSize.X;
            var fbH = _window.FramebufferSize.Y;
            if (Inspect.Active)
            {
                Inspect.Tick(fbW, fbH);
            }
            else
            {
                Benchmark.Tick(dt, _lastUpdateMs, renderMs, RenderDebug.GpuMs);
                Benchmark.CaptureFrame(fbW, fbH);
            }

            // The benchmark/inspect modes close the window when their scripted run completes.
            if (Benchmark.Finished || Inspect.Finished) _window.Close();
        }

        private static void OnClosing()
        {
            Profiler.Stop();
            StateEngine.Exit();
            Gpu.Shutdown();
        }
    }
}

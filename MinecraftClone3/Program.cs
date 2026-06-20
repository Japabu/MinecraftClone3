using System;
using System.Diagnostics;
using System.Globalization;
using MinecraftClone3.States;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

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

        private readonly Stopwatch _workTimer = new Stopwatch();
        private readonly Stopwatch _swapTimer = new Stopwatch();
        // Measures the gap from end-of-render to the next OnUpdateFrame: OpenTK's loop runs
        // NewInputFrame + ProcessWindowEvents (the GLFW poll, where an async/vsync present surfaces on
        // Linux/GLX) there, which none of our other timers see. A frameMs spike that isn't in
        // update/render/swap shows up here.
        private readonly Stopwatch _gapTimer = new Stopwatch();
        private double _lastUpdateMs;
        private double _lastSwapMs;
        private double _lastGapMs;
        private int _updateCalls;

        // GL_TIME_ELAPSED whole-frame timer queries: the actual GPU time for the render commands, separating
        // "GPU genuinely slow" from "GPU fast, the wait is vsync/present/event overhead". Created lazily once
        // the GL context exists. A ring (not a 1-frame ping-pong) harvested newest-ready, because with vsync
        // off the CPU runs several frames ahead of the GPU, so last frame's query usually isn't done yet — a
        // 1-frame read would perpetually miss and freeze gpuMs at a stale value. See GpuTimers for the detail.
        private const int GpuRing = 8;
        private int[] _gpuQueries;
        private bool[] _gpuPending;
        private long[] _gpuQueryFrame;
        private bool _gpuQueriesReady;
        private long _gpuFrame;
        private long _gpuLastHarvested = -1;
        private double _lastGpuMs;

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            if (_gapTimer.IsRunning) _lastGapMs = _gapTimer.Elapsed.TotalMilliseconds;

            base.OnUpdateFrame(e);

            _updateCalls++;
            _workTimer.Restart();
            StateEngine.Update();
            _lastUpdateMs = _workTimer.Elapsed.TotalMilliseconds;
        }

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            base.OnRenderFrame(e);

            if (!_gpuQueriesReady)
            {
                _gpuQueries = new int[GpuRing];
                _gpuPending = new bool[GpuRing];
                _gpuQueryFrame = new long[GpuRing];
                for (var i = 0; i < GpuRing; i++) _gpuQueries[i] = GL.GenQuery();
                _gpuQueriesReady = true;
            }

            // Harvest the newest ring slot whose result has arrived (only reads when available, so no stall).
            var best = -1;
            var bestFrame = _gpuLastHarvested;
            for (var i = 0; i < GpuRing; i++)
            {
                if (!_gpuPending[i]) continue;
                if (_gpuQueryFrame[i] <= _gpuLastHarvested) { _gpuPending[i] = false; continue; }
                GL.GetQueryObject(_gpuQueries[i], GetQueryObjectParam.QueryResultAvailable, out int available);
                if (available != 0 && _gpuQueryFrame[i] > bestFrame) { best = i; bestFrame = _gpuQueryFrame[i]; }
            }
            if (best >= 0)
            {
                GL.GetQueryObject(_gpuQueries[best], GetQueryObjectParam.QueryResult, out long elapsedNs);
                _lastGpuMs = elapsedNs / 1_000_000.0;
                _gpuPending[best] = false;
                _gpuLastHarvested = bestFrame;
            }

            var writeQuery = (int) (_gpuFrame % GpuRing);
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            _workTimer.Restart();
            GL.BeginQuery(QueryTarget.TimeElapsed, _gpuQueries[writeQuery]);
            StateEngine.Render();
            GL.EndQuery(QueryTarget.TimeElapsed);
            var renderMs = _workTimer.Elapsed.TotalMilliseconds;
            var renderAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            _gpuQueryFrame[writeQuery] = _gpuFrame;
            _gpuPending[writeQuery] = true;
            _gpuFrame++;

            // Mirror the frame timings for the on-screen diagnostics overlay (F3). It is drawn inside the
            // StateEngine.Render above, so it reads the previous frame's values — invisible for a HUD. Frame
            // time is EMA-smoothed so the displayed FPS doesn't jitter.
            RenderDebug.FrameMs = RenderDebug.FrameMs <= 0 ? e.Time * 1000 : RenderDebug.FrameMs * 0.92 + e.Time * 1000 * 0.08;
            RenderDebug.GpuMs = _lastGpuMs;
            RenderDebug.UpdateMs = _lastUpdateMs;

            // e.Time is the wall-clock interval since the last render frame (catches real fps drops).
            // renderMs/updateMs are CPU work; swapMs is the SwapBuffers call; gapMs is OpenTK's
            // poll/present gap; gpuMs is actual GPU render time (GL_TIME_ELAPSED). When frameMs is high
            // but update/render/swap are small, gapMs vs gpuMs says whether it's present/event overhead
            // or the GPU itself — the two stalls a CPU sampler can't see.
            Profiler.Record(e.Time, _lastUpdateMs, renderMs, _lastSwapMs, _lastGapMs, _lastGpuMs,
                _updateCalls, renderAlloc);
            Benchmark.Tick(e.Time, _lastUpdateMs, renderMs, _lastGpuMs);
            Benchmark.CaptureFrame(FramebufferSize.X, FramebufferSize.Y);
            _updateCalls = 0;

            _swapTimer.Restart();
            SwapBuffers();
            _lastSwapMs = _swapTimer.Elapsed.TotalMilliseconds;

            _gapTimer.Restart();

            // The benchmark closes the window when its scripted run completes; the report has already printed.
            if (Benchmark.Finished) Close();
        }

        protected override void OnUnload()
        {
            Profiler.Stop();
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

            // RenderDoc can only hook our GL context over GLX (X11); under native Wayland GLFW makes an EGL
            // context it can't capture ("unknown window"). Force the X11 backend when launched under RenderDoc
            // (it sets RENDERDOC_CAPOPTS) or when MC3_FORCE_X11=1; normal runs keep native Wayland.
            if (Environment.GetEnvironmentVariable("RENDERDOC_CAPOPTS") != null ||
                Environment.GetEnvironmentVariable("MC3_FORCE_X11") == "1")
                GLFW.InitHint(InitHintPlatform.Platform, Platform.X11);

            // Saved graphics options seed the window so it opens with the user's vsync/fullscreen choice;
            // runtime changes go through the GraphicsSettings setters (which push onto the live window).
            GraphicsSettings.Load();

            // Benchmark mode (--benchmark): boot straight into the automated flythrough. VSync MUST be off so we
            // measure uncapped frame rate, and the deterministic overrides apply in-memory (no save).
            Benchmark.Configure(args);
            if (Benchmark.Enabled) Benchmark.ApplySettings();

            var nativeWindowSettings = new NativeWindowSettings
            {
                ClientSize = new Vector2i(1280, 720),
                Title = "MinecraftClone3",
                Profile = ContextProfile.Core,
                // macOS only exposes OpenGL up to 4.1 Core.
                APIVersion = new Version(4, 1),
                Vsync = Benchmark.Enabled ? VSyncMode.Off : GraphicsSettings.VSync,
                WindowState = GraphicsSettings.Fullscreen ? WindowState.Fullscreen : WindowState.Normal
            };

            var gameWindowSettings = new GameWindowSettings
            {
                // OpenTK 4.9 runs OnUpdateFrame and OnRenderFrame at one shared rate = UpdateFrequency.
                // 0 = uncapped: the benchmark must run unthrottled to measure true engine throughput (the
                // 120 Hz cap = 8.33 ms/frame is what pinned observed FPS at ~118 regardless of GPU/CPU).
                UpdateFrequency = Benchmark.Enabled ? 0 : 120
            };

            Window = new GameClient(gameWindowSettings, nativeWindowSettings);
            Window.Run();
        }
    }
}

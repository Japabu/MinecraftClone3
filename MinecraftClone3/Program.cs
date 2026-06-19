using System;
using System.Diagnostics;
using System.Globalization;
using MinecraftClone3.States;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Util;
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

        // GL_TIME_ELAPSED timer queries (ping-pong so the result is read a frame later, never stalling):
        // measures the actual GPU time for the render commands, separating "GPU genuinely slow" from
        // "GPU fast, the wait is vsync/present/event overhead". Created lazily once the GL context exists.
        private int _gpuQueryA, _gpuQueryB;
        private bool _gpuQueriesReady;
        private long _gpuFrame;
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
                _gpuQueryA = GL.GenQuery();
                _gpuQueryB = GL.GenQuery();
                _gpuQueriesReady = true;
            }

            var writeQuery = (_gpuFrame & 1) == 0 ? _gpuQueryA : _gpuQueryB;
            var readQuery = (_gpuFrame & 1) == 0 ? _gpuQueryB : _gpuQueryA;

            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            _workTimer.Restart();
            GL.BeginQuery(QueryTarget.TimeElapsed, writeQuery);
            StateEngine.Render();
            GL.EndQuery(QueryTarget.TimeElapsed);
            var renderMs = _workTimer.Elapsed.TotalMilliseconds;
            var renderAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            // Read the previous frame's GPU time (its result is ready by now, so no stall).
            if (_gpuFrame > 0)
            {
                GL.GetQueryObject(readQuery, GetQueryObjectParam.QueryResultAvailable, out int available);
                if (available != 0)
                {
                    GL.GetQueryObject(readQuery, GetQueryObjectParam.QueryResult, out long elapsedNs);
                    _lastGpuMs = elapsedNs / 1_000_000.0;
                }
            }
            _gpuFrame++;

            // e.Time is the wall-clock interval since the last render frame (catches real fps drops).
            // renderMs/updateMs are CPU work; swapMs is the SwapBuffers call; gapMs is OpenTK's
            // poll/present gap; gpuMs is actual GPU render time (GL_TIME_ELAPSED). When frameMs is high
            // but update/render/swap are small, gapMs vs gpuMs says whether it's present/event overhead
            // or the GPU itself — the two stalls a CPU sampler can't see.
            Profiler.Record(e.Time, _lastUpdateMs, renderMs, _lastSwapMs, _lastGapMs, _lastGpuMs,
                _updateCalls, renderAlloc);
            _updateCalls = 0;

            _swapTimer.Restart();
            SwapBuffers();
            _lastSwapMs = _swapTimer.Elapsed.TotalMilliseconds;

            _gapTimer.Restart();
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
                // OpenTK 4.9 runs OnUpdateFrame and OnRenderFrame at one shared rate = UpdateFrequency.
                UpdateFrequency = 120
            };

            Window = new GameClient(gameWindowSettings, nativeWindowSettings);
            Window.Run();
        }
    }
}

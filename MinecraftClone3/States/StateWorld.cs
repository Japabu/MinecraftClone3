using System;
using System.Diagnostics;
using System.Net.Sockets;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Networking;
using MinecraftClone3API.Util;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3.States
{
    internal class StateWorld : StateBase
    {
        private const string ServerAddress = "127.0.0.1";

        // Placeholder until the seed-derived spawn arrives in LoginAccept; the loading gate snaps the
        // player onto it once the surrounding chunks have streamed in (see UpdateLoading).
        private static readonly Vector3 SpawnPos = new Vector3(0, 80, 0);

        private const double LoadingTimeoutSeconds = 30;

        private readonly GameWindow _window;
        private readonly bool _multiplayer;
        private readonly bool _benchmark;

        private readonly EntityPlayer _player;
        private readonly WorldClient _world;

        // Singleplayer only: the in-process server the loopback client talks to.
        private readonly WorldServer _integratedServer;
        private readonly ServerNetwork _network;

        private readonly bool _connectionFailed;

        private bool _loading = true;
        private bool _spawnApplied;
        private int _lastRenderDistanceChunks = -1;
        private readonly Stopwatch _loadingTimer = Stopwatch.StartNew();
        private Texture _loadingBackground;

        private readonly Stopwatch _phaseTimer = new Stopwatch();

        // The whole simulation (player physics, server tick, network pump, send-move) runs at a fixed
        // 20 tps off this accumulator, while input/look/camera/render keep running every display frame and
        // interpolate. _simTimer measures real time between Update calls (the loop is the display rate).
        private readonly Stopwatch _simTimer = new Stopwatch();
        private double _simAccumulator;
        private const double MaxFrameTime = 0.25;
        private const int MaxCatchUpTicks = 5;

        /// <summary>Singleplayer: runs the given world in an in-process server over a loopback connection.</summary>
        public StateWorld(GameWindow window, WorldInfo world) : this(window, false, world, false) { }

        /// <summary>Benchmark: singleplayer world driven by the automated <see cref="Benchmark"/> flythrough.</summary>
        public StateWorld(GameWindow window, WorldInfo world, bool benchmark) : this(window, false, world, benchmark) { }

        /// <summary>Multiplayer: connects to the dedicated server over TCP.</summary>
        public StateWorld(GameWindow window, bool multiplayer = false) : this(window, multiplayer, null, false) { }

        private StateWorld(GameWindow window, bool multiplayer, WorldInfo world, bool benchmark)
        {
            _window = window;
            _multiplayer = multiplayer;
            _benchmark = benchmark;

            // Grab the cursor so relative mouse movement drives the camera (FPS-style). The benchmark drives the
            // camera itself and runs unattended, so it must NOT grab the cursor (that would trap the user's mouse).
            _window.CursorState = benchmark ? CursorState.Hidden : CursorState.Grabbed;
            PlayerController.ResetMouse();

            _player = new EntityPlayer {Position = SpawnPos};
            PlayerController.SetEntity(_player);

            IConnection connection;
            if (multiplayer)
            {
                try
                {
                    var tcp = new TcpClient();
                    tcp.Connect(ServerAddress, ServerNetwork.DefaultPort);
                    connection = new TcpConnection(tcp);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Could not connect to {ServerAddress}:{ServerNetwork.DefaultPort}");
                    Logger.Exception(ex);
                    _connectionFailed = true;
                    return;
                }
            }
            else
            {
                _integratedServer = new WorldServer(world.Seed, world.Directory);
                _network = new ServerNetwork(_integratedServer);
                var loopback = new LoopbackConnection();
                _network.AddConnection(loopback.ServerSide);
                connection = loopback.ClientSide;
            }

            _world = new WorldClient(connection);
            _world.Login();

            Profiler.World = _world;
            Profiler.Network = _network;
            Profiler.Server = _integratedServer;
            ChunkTracer.Multiplayer = multiplayer;

            ApplyRenderDistance();
        }

        /// <summary>Pushes the render-distance setting through the coupled radius chain. In singleplayer the
        /// client owns the integrated server, so the slider drives the server view/load radius and the client
        /// cache too; in multiplayer only the client DRAW distance follows it (WorldRenderer reads the setting
        /// live), and the cache stays at its safe default since the client can't know the remote view distance.</summary>
        private void ApplyRenderDistance()
        {
            var chunks = GraphicsSettings.RenderDistanceChunks;
            _lastRenderDistanceChunks = chunks;

            if (_integratedServer == null) return;

            _network.ViewDistance = chunks * Chunk.Size;
            _integratedServer.TerrainRadius = chunks + 1;
            _world.CacheDistance = chunks * Chunk.Size + WorldClient.CacheHysteresis;
        }

        public override void Update(bool focused)
        {
            if (_connectionFailed)
            {
                StateEngine.ReplaceState(new GuiMainMenu(_window));
                return;
            }

            if (_loading)
            {
                UpdateLoading();
                return;
            }

            if (GraphicsSettings.RenderDistanceChunks != _lastRenderDistanceChunks)
                ApplyRenderDistance();

            // The automated benchmark drives the camera itself (no player input / no pause overlay).
            var active = focused && !_benchmark;
            if (active && _window.KeyboardState.IsKeyPressed(Keys.Escape))
            {
                StateEngine.AddOverlay(new GuiPauseMenu(_window));
                active = false;
            }

            // Singleplayer freezes the world while paused; multiplayer can't pause a shared server. The
            // benchmark always pumps so chunks keep streaming/regenerating even with the window unfocused.
            var simulate = focused || _multiplayer || _benchmark;
            if (simulate)
            {
                var dt = _simTimer.Elapsed.TotalSeconds;
                _simTimer.Restart();
                if (dt > MaxFrameTime) dt = MaxFrameTime;
                _simAccumulator += dt;

                var steps = 0;
                while (_simAccumulator >= PlayerPhysics.TickSeconds && steps < MaxCatchUpTicks)
                {
                    Tick(active);
                    _simAccumulator -= PlayerPhysics.TickSeconds;
                    steps++;
                }
                // Don't let a long stall (alt-tab, GC) spiral into an ever-growing tick backlog.
                if (steps == MaxCatchUpTicks) _simAccumulator = 0;
            }
            else
            {
                _simTimer.Restart();
                _simAccumulator = 0;
            }

            PlayerController.ApplyInterpolation(simulate ? (float) (_simAccumulator / PlayerPhysics.TickSeconds) : 1f);

            if (_benchmark)
            {
                // The automated flythrough drives the camera (and issues edits) each frame; overwrites the
                // interpolation above (it sets Position/Prev/Interpolated directly).
                Benchmark.DriveCamera(_player, _world);
                PlayerController.Camera.Update();
            }
            else if (active) PlayerController.UpdateFrame(_window, _world);

            var c = GC.GetAllocatedBytesForCurrentThread();
            _phaseTimer.Restart();
            _world.Update();
            Profiler.AddClientTime(_phaseTimer.Elapsed.TotalMilliseconds);
            Profiler.AddClientAlloc(GC.GetAllocatedBytesForCurrentThread() - c);
        }

        /// <summary>One fixed 20 tps simulation step: the player physics (only when <paramref name="stepPlayer"/>,
        /// i.e. focused and not paused), then the server tick, network pump, and the client's send-move.
        /// Called zero or more times per display frame by the accumulator in <see cref="Update"/>.</summary>
        private void Tick(bool stepPlayer)
        {
            if (stepPlayer) PlayerController.Tick(_window, _world);

            var a = GC.GetAllocatedBytesForCurrentThread();
            _phaseTimer.Restart();
            _integratedServer?.Update();
            Profiler.AddServerTime(_phaseTimer.Elapsed.TotalMilliseconds);
            Profiler.AddServerAlloc(GC.GetAllocatedBytesForCurrentThread() - a);

            a = GC.GetAllocatedBytesForCurrentThread();
            _phaseTimer.Restart();
            _network?.Pump();
            Profiler.AddNetworkTime(_phaseTimer.Elapsed.TotalMilliseconds);
            Profiler.AddNetworkAlloc(GC.GetAllocatedBytesForCurrentThread() - a);

            var c = GC.GetAllocatedBytesForCurrentThread();
            _phaseTimer.Restart();
            _world.SendMove(_player);
            Profiler.AddClientTime(_phaseTimer.Elapsed.TotalMilliseconds);
            Profiler.AddClientAlloc(GC.GetAllocatedBytesForCurrentThread() - c);
        }

        /// <summary>Pumps the world during the join handshake: applies the seed-derived spawn from
        /// LoginAccept, then hands control to the player once the server signals the spawn area is
        /// streamed (PlayerReady) and the client has actually decoded those chunks (so gravity has
        /// ground to land on). The same packet path drives this for singleplayer and multiplayer; the
        /// timeout is only an anti-hang safety against a dropped signal.</summary>
        private void UpdateLoading()
        {
            _integratedServer?.Update();
            _network?.Pump();
            _world.Update();

            if (_world.SpawnReceived && !_spawnApplied)
            {
                _player.Position = _world.SpawnPosition;
                _player.PrevPosition = _world.SpawnPosition;
                _player.InterpolatedPosition = _world.SpawnPosition;
                _player.Velocity = Vector3.Zero;
                _spawnApplied = true;
            }

            if (_spawnApplied) _world.SendMove(_player);

            var ready = _spawnApplied && _world.Ready && SpawnChunksApplied();
            if (ready || _loadingTimer.Elapsed.TotalSeconds > LoadingTimeoutSeconds)
            {
                _loading = false;
                // Start the sim clock fresh so the first frame after loading doesn't see a huge dt.
                _simTimer.Restart();
                _simAccumulator = 0;
                PlayerController.ResetMouse();
                // The camera never updated during loading; sync it to the spawn so the first world
                // frame doesn't flash the default origin view before PlayerController.UpdateFrame runs.
                PlayerController.Camera.Update();
                // Anchor the automated flythrough at the spawn now that terrain is under the player.
                if (_benchmark) Benchmark.Begin(_player.Position);
            }
        }

        // PlayerReady means the server has *sent* the spawn column; this confirms the client's apply
        // thread has actually decoded it into LoadedChunks, so the player has ground before physics start.
        private bool SpawnChunksApplied()
        {
            var feetChunk = WorldBase.ChunkInWorld(_player.Position.ToVector3i());
            return _world.LoadedChunks.ContainsKey(feetChunk) &&
                   _world.LoadedChunks.ContainsKey(feetChunk - new Vector3i(0, 1, 0));
        }

        public override void Render()
        {
            if (_connectionFailed) return;

            if (_loading)
            {
                RenderLoading();
                return;
            }

            var aspect = (float)_window.FramebufferSize.X / _window.FramebufferSize.Y;
            var projection = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(GraphicsSettings.Fov), aspect, 0.01f, 512);
            WorldRenderer.RenderWorld(_world, projection);

            if (!Profiler.Recording && !RenderDebug.ShowDiagnostics && !RenderDebug.ShowControls) return;

            RenderState.Set(new GlState
            {
                Blend = true,
                BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            });

            var y = 4;
            if (Profiler.Recording)
            {
                Font.DrawString("● REC", 4, y, 2, new Color4(1f, 0.3f, 0.3f, 1f));
                y += Font.LineHeight(2) + 4;
            }

            if (RenderDebug.ShowDiagnostics) y = DrawDiagnostics(y);
            if (RenderDebug.ShowControls) DrawControls(y);
        }

        private static readonly Color4 OverlayText = new Color4(1f, 1f, 1f, 1f);
        private static readonly Color4 OverlayHeader = new Color4(0.55f, 0.8f, 1f, 1f);

        private void RenderLoading()
        {
            _loadingBackground ??= ResourceReader.ReadTexture("System/Textures/Gui/ResourceLoadingBackground.png");
            GuiRenderer.DrawTexture(_loadingBackground,
                new Rectangle(0, 0, (int)ScaledResolution.GuiResolution.X, (int)ScaledResolution.GuiResolution.Y), null);

            const string msg = "Generating world...";
            const int scale = 3;
            var x = (int)ScaledResolution.GuiResolution.X / 2 - Font.MeasureWidth(msg, scale) / 2;
            var y = (int)ScaledResolution.GuiResolution.Y / 2 - Font.LineHeight(scale) / 2;
            Font.DrawString(msg, x, y, scale, OverlayText);
        }

        // Fixed keybinds for the F1 controls overlay (key column, description column).
        private static readonly (string Key, string Desc)[] ControlsRows =
        {
            ("Move", "WASD"),
            ("Jump", "Space"),
            ("Sprint", "Ctrl"),
            ("Toggle fly", "Double Space"),
            ("Fly up / down", "Space / Shift"),
            ("Look", "Mouse"),
            ("Break / Place", "Left / Right Click"),
            ("Blocks", "number keys (keybindings.txt)"),
            ("Pause", "Esc"),
            ("", ""),
            ("F1", "controls (this)"),
            ("F3", "debug overlay"),
            ("F4", "chunk borders"),
            ("F7", "shadow factor"),
            ("F10", "profiler record (CSV)")
        };

        private int DrawDiagnostics(int y)
        {
            const int scale = 2;
            var lh = Font.LineHeight(scale) + 2;

            var frameMs = RenderDebug.FrameMs;
            var fps = frameMs > 0 ? 1000.0 / frameMs : 0;
            Font.DrawString($"FPS {fps:0}  ({frameMs:0.0} ms)", 4, y, scale, OverlayText); y += lh;
            Font.DrawString($"gpu {RenderDebug.GpuMs:0.0} ms   cpu upd {RenderDebug.UpdateMs:0.0} ms", 4, y, scale, OverlayText); y += lh;

            Font.DrawString($"chunks drawn {RenderDebug.DrawnChunks} / {_world.RenderList.Count}", 4, y, scale, OverlayText); y += lh;

            var shadows = RenderDebug.ShadowPass ? "on" : "off";
            Font.DrawString($"shadows {shadows}   loaded {_world.LoadedChunkCount}" +
                            $"   mesh {_world.MeshQueueDepth}   upload {_world.UploadQueueDepth}", 4, y, scale, OverlayText); y += lh;

            var stage = _integratedServer != null ? $"   stage {_integratedServer.StageQueueDepth}" : "";
            Font.DrawString($"apply {_world.ApplyQueueDepth}   ready {_world.RenderReadyQueueDepth}" +
                            $"   dispose {_world.DisposeQueueDepth}{stage}", 4, y, scale, OverlayText); y += lh;

            var p = _player.Position;
            var c = WorldBase.ChunkInWorld(p.ToVector3i());
            Font.DrawString($"pos {p.X:0.0} {p.Y:0.0} {p.Z:0.0}   chunk {c.X} {c.Y} {c.Z}", 4, y, scale, OverlayText); y += lh;

            return y + 6;
        }

        private void DrawControls(int y)
        {
            const int scale = 2;
            var lh = Font.LineHeight(scale) + 2;

            Font.DrawString("Controls", 4, y, scale, OverlayHeader); y += lh;
            foreach (var row in ControlsRows)
            {
                if (row.Key.Length > 0)
                {
                    Font.DrawString(row.Key, 8, y, scale, OverlayText);
                    Font.DrawString(row.Desc, 260, y, scale, OverlayText);
                }
                y += lh;
            }
        }

        public override void Exit()
        {
            Profiler.Stop();
            Profiler.World = null;
            Profiler.Network = null;
            Profiler.Server = null;
            _world?.Disconnect();
            _network?.Stop();
            _integratedServer?.Unload();
        }
    }
}

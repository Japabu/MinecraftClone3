using System;
using System.Diagnostics;
using System.Net.Sockets;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
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

        // Matches ServerNetwork.SpawnPosition; the server is authoritative but the client positions
        // its camera here immediately so there's no first-frame jump before LoginAccept arrives.
        private static readonly Vector3 SpawnPos = new Vector3(0, 12, 0);

        private readonly GameWindow _window;
        private readonly bool _multiplayer;

        private readonly EntityPlayer _player;
        private readonly WorldClient _world;

        // Singleplayer only: the in-process server the loopback client talks to.
        private readonly WorldServer _integratedServer;
        private readonly ServerNetwork _network;

        private readonly bool _connectionFailed;

        private readonly Stopwatch _phaseTimer = new Stopwatch();

        public StateWorld(GameWindow window, bool multiplayer = false)
        {
            _window = window;
            _multiplayer = multiplayer;

            // Grab the cursor so relative mouse movement drives the camera (FPS-style).
            _window.CursorState = CursorState.Grabbed;
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
                _integratedServer = new WorldServer();
                _network = new ServerNetwork(_integratedServer);
                var loopback = new LoopbackConnection();
                _network.AddConnection(loopback.ServerSide);
                connection = loopback.ClientSide;
            }

            _world = new WorldClient(connection);
            _world.Login();

            Profiler.World = _world;
            Profiler.Network = _network;
        }

        public override void Update(bool focused)
        {
            if (_connectionFailed)
            {
                StateEngine.ReplaceState(new GuiMainMenu(_window));
                return;
            }

            if (focused)
            {
                if (_window.KeyboardState.IsKeyPressed(Keys.Escape))
                    StateEngine.AddOverlay(new GuiPauseMenu(_window));
                else
                    PlayerController.Update(_window, _world);
            }

            // Singleplayer freezes the world while paused; multiplayer can't pause a shared server.
            if (focused || _multiplayer)
            {
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
            }

            var c = GC.GetAllocatedBytesForCurrentThread();
            _phaseTimer.Restart();
            _world.SendMove(_player);
            _world.Update();
            Profiler.AddClientTime(_phaseTimer.Elapsed.TotalMilliseconds);
            Profiler.AddClientAlloc(GC.GetAllocatedBytesForCurrentThread() - c);
        }

        public override void Render()
        {
            if (_connectionFailed) return;

            var aspect = (float)_window.FramebufferSize.X / _window.FramebufferSize.Y;
            var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60), aspect, 0.01f, 512);
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

        // Fixed keybinds for the F1 controls overlay (key column, description column).
        private static readonly (string Key, string Desc)[] ControlsRows =
        {
            ("Move", "WASD"),
            ("Up / Down", "Space / Shift"),
            ("Look", "Mouse"),
            ("Break / Place", "Left / Right Click"),
            ("Blocks", "number keys (keybindings.txt)"),
            ("Pause", "Esc"),
            ("", ""),
            ("F1", "controls (this)"),
            ("F3", "debug overlay"),
            ("F4", "chunk borders"),
            ("F5", "occlusion culling on/off"),
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

            var occlusion = RenderDebug.DisableOcclusionCulling ? "off" : "on";
            Font.DrawString($"chunks drawn {RenderDebug.DrawnChunks} / {_world.RenderList.Count}" +
                            $"   visited {RenderDebug.VisitedChunks}   (cull {occlusion})", 4, y, scale, OverlayText); y += lh;

            var shadows = RenderDebug.ShadowPass ? "on" : "off";
            Font.DrawString($"shadows {shadows}   loaded {_world.LoadedChunkCount}" +
                            $"   mesh {_world.MeshQueueDepth}   upload {_world.UploadQueueDepth}", 4, y, scale, OverlayText); y += lh;

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
            _world?.Disconnect();
            _network?.Stop();
            _integratedServer?.Unload();
        }
    }
}

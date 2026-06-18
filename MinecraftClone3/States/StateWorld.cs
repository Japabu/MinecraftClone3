using System;
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
                _integratedServer?.Update();
                Profiler.AddServerAlloc(GC.GetAllocatedBytesForCurrentThread() - a);

                a = GC.GetAllocatedBytesForCurrentThread();
                _network?.Pump();
                Profiler.AddNetworkAlloc(GC.GetAllocatedBytesForCurrentThread() - a);
            }

            var c = GC.GetAllocatedBytesForCurrentThread();
            _world.SendMove(_player);
            _world.Update();
            Profiler.AddClientAlloc(GC.GetAllocatedBytesForCurrentThread() - c);
        }

        public override void Render()
        {
            if (_connectionFailed) return;

            var aspect = (float)_window.FramebufferSize.X / _window.FramebufferSize.Y;
            var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60), aspect, 0.01f, 512);
            WorldRenderer.RenderWorld(_world, projection);

            if (Profiler.Recording)
            {
                RenderState.Set(new GlState
                {
                    Blend = true,
                    BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
                });
                Font.DrawString("● REC", 4, 4, 2, new Color4(1f, 0.3f, 0.3f, 1f));
            }
        }

        public override void Exit()
        {
            Profiler.Stop();
            Profiler.World = null;
            _world?.Disconnect();
            _network?.Stop();
            _integratedServer?.Unload();
        }
    }
}

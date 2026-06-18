using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace MinecraftClone3.States
{
    internal class StateWorld : StateBase
    {
        // Terrain peaks at roughly y=7 (see WorldServer heightmap), so spawn well above it
        // and float a torch a couple of blocks below the camera.
        private static readonly Vector3i TorchPos = new Vector3i(0, 10, 0);
        private static readonly Vector3 SpawnPos = new Vector3(0, 12, 0);

        private readonly GameWindow _window;
        private readonly EntityPlayer _player;
        private readonly WorldServer _world;

        private bool _torchPlaced;

        public StateWorld(GameWindow window)
        {
            _window = window;
            // Grab the cursor so relative mouse movement drives the camera (FPS-style).
            _window.CursorState = CursorState.Grabbed;

            _player = new EntityPlayer {Position = SpawnPos};
            PlayerController.SetEntity(_player);

            _world = new WorldServer();
            _world.PlayerEntities.Add(_player);
        }

        public override void Update()
        {
            PlayerController.Update(_window, _world);
            _world.Update();

            // Place the torch only once its chunk has been generated and loaded; doing it in the
            // constructor would race the terrain generator and discard the generated chunk.
            if (!_torchPlaced && !_world.IsBlockInEmptyChunk(TorchPos))
            {
                _world.SetBlock(TorchPos, GameRegistry.GetBlock("Vanilla:Torch"));
                _torchPlaced = true;
            }
        }

        public override void Render()
        {
            var aspect = (float) _window.FramebufferSize.X / _window.FramebufferSize.Y;
            var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60), aspect, 0.01f, 512);
            WorldRenderer.RenderWorld(_world, projection);
        }

        public override void Exit() => _world.Unload();
    }
}

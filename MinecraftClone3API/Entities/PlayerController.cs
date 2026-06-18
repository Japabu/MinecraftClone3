using System;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MinecraftClone3API.Entities
{
    public static class PlayerController
    {
        public static EntityPlayer PlayerEntity;
        public static Camera Camera = new Camera();

        private static BlockRaytraceResult _blockRaytrace;
        private static string _currentBlock = "Vanilla:Stone";
        private static bool _skipMouseDelta;

        public static void SetEntity(EntityPlayer playerEntity)
        {
            PlayerEntity = playerEntity;
            Camera.ParentEntity = playerEntity;
        }

        public static void Update(GameWindow window, WorldServer world)
        {
            _blockRaytrace = world.BlockRaytrace(PlayerEntity.Position, PlayerEntity.Forward, 8);

            if (!window.IsFocused) return;

            var ks = window.KeyboardState;
            var a = Vector3.Zero;
            if (ks.IsKeyDown(Keys.A))
                a.X -= 1;
            if (ks.IsKeyDown(Keys.D))
                a.X += 1;
            if (ks.IsKeyDown(Keys.LeftShift))
                a.Y -= 1;
            if (ks.IsKeyDown(Keys.Space))
                a.Y += 1;
            if (ks.IsKeyDown(Keys.S))
                a.Z -= 1;
            if (ks.IsKeyDown(Keys.W))
                a.Z += 1;
            if (Math.Abs(a.LengthSquared) > 0.0001f)
                PlayerEntity.Move(a.Normalized() * 0.08f);

            foreach (var keybinding in ClientResources.Keybindings)
            {
                if (ks.IsKeyDown(keybinding.Key)) _currentBlock = keybinding.Value;
            }

            var ms = window.MouseState;
            var delta = ms.Delta;
            if (_skipMouseDelta)
            {
                delta = Vector2.Zero;
                _skipMouseDelta = false;
            }
            PlayerEntity.Rotate(-delta.Y * 0.008f, -delta.X * 0.008f);

            if (ms.IsButtonDown(MouseButton.Left) && !ms.WasButtonDown(MouseButton.Left))
                BreakBlock(world);
            if (ms.IsButtonDown(MouseButton.Right) && !ms.WasButtonDown(MouseButton.Right))
                PlaceBlock(world);

            Camera.Update();
        }

        private static void BreakBlock(WorldServer world)
        {
            if (_blockRaytrace == null) return;
            world.SetBlock(_blockRaytrace.BlockPos, BlockRegistry.BlockAir);
        }

        private static void PlaceBlock(WorldServer world)
        {
            if (_blockRaytrace == null) return;
            world.PlaceBlock(PlayerEntity, _blockRaytrace.BlockPos + _blockRaytrace.Face.GetNormali(), GameRegistry.GetBlock(_currentBlock));

            Logger.Debug(PlayerEntity.Position + ":" + _blockRaytrace.BlockPos);
        }

        /// <summary>Discards the next mouse delta so re-grabbing the cursor (window refocus,
        /// closing a menu) doesn't snap the camera.</summary>
        public static void ResetMouse() => _skipMouseDelta = true;

        public static void Render(Camera camera, Matrix4 projection)
        {
            if (_blockRaytrace == null) return;

            BoundingBoxRenderer.Render(_blockRaytrace.BoundingBox, _blockRaytrace.BlockPos.ToVector3(), 1.01f, camera,
                projection);
        }
    }
}

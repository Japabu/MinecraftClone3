using System;
using System.Diagnostics;
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

        private const float FlySpeed = 16f;
        private const float SprintMultiplier = 3f;
        private const float MaxStepSeconds = 0.1f;
        private const float DoubleTapSeconds = 0.3f;

        private static BlockRaytraceResult _blockRaytrace;
        private static string _currentBlock = "Vanilla:Stone";
        private static bool _skipMouseDelta;
        private static readonly Stopwatch _moveTimer = Stopwatch.StartNew();
        private static readonly Stopwatch _spaceTapTimer = Stopwatch.StartNew();
        private static float _physicsAccumulator;

        public static void SetEntity(EntityPlayer playerEntity)
        {
            PlayerEntity = playerEntity;
            Camera.ParentEntity = playerEntity;
        }

        public static void Update(GameWindow window, WorldBase world)
        {
            _blockRaytrace = world.BlockRaytrace(PlayerEntity.RenderPosition + PlayerEntity.EyeOffset,
                PlayerEntity.Forward, 8);

            var dt = (float) _moveTimer.Elapsed.TotalSeconds;
            _moveTimer.Restart();
            if (dt > MaxStepSeconds) dt = MaxStepSeconds;

            if (!window.IsFocused) return;

            var ks = window.KeyboardState;
            var ms = window.MouseState;

            var delta = ms.Delta;
            if (_skipMouseDelta)
            {
                delta = Vector2.Zero;
                _skipMouseDelta = false;
            }
            PlayerEntity.Rotate(-delta.Y * 0.008f, -delta.X * 0.008f);

            if (ks.IsKeyPressed(Keys.Space))
            {
                if (_spaceTapTimer.Elapsed.TotalSeconds < DoubleTapSeconds)
                {
                    PlayerEntity.Flying = !PlayerEntity.Flying;
                    PlayerEntity.Velocity = Vector3.Zero;
                }
                _spaceTapTimer.Restart();
            }

            if (PlayerEntity.Flying) UpdateFlying(ks, dt);
            else UpdateWalking(ks, dt, world);

            foreach (var keybinding in ClientResources.Keybindings)
            {
                if (ks.IsKeyDown(keybinding.Key)) _currentBlock = keybinding.Value;
            }

            if (ks.IsKeyPressed(Keys.F1)) RenderDebug.ShowControls = !RenderDebug.ShowControls;
            if (ks.IsKeyPressed(Keys.F3)) RenderDebug.ShowDiagnostics = !RenderDebug.ShowDiagnostics;
            if (ks.IsKeyPressed(Keys.F4)) ChunkBorderRenderer.Enabled = !ChunkBorderRenderer.Enabled;
            if (ks.IsKeyPressed(Keys.F7)) RenderDebug.ShadowFactor = !RenderDebug.ShadowFactor;
            if (ks.IsKeyPressed(Keys.F10)) Profiler.Toggle();

            if (ms.IsButtonDown(MouseButton.Left) && !ms.WasButtonDown(MouseButton.Left))
                BreakBlock(world);
            if (ms.IsButtonDown(MouseButton.Right) && !ms.WasButtonDown(MouseButton.Right))
                PlaceBlock(world);

            Camera.Update();
        }

        private static void UpdateFlying(KeyboardState ks, float dt)
        {
            var a = Vector3.Zero;
            if (ks.IsKeyDown(Keys.A)) a.X -= 1;
            if (ks.IsKeyDown(Keys.D)) a.X += 1;
            if (ks.IsKeyDown(Keys.LeftShift)) a.Y -= 1;
            if (ks.IsKeyDown(Keys.Space)) a.Y += 1;
            if (ks.IsKeyDown(Keys.S)) a.Z -= 1;
            if (ks.IsKeyDown(Keys.W)) a.Z += 1;
            if (Math.Abs(a.LengthSquared) > 0.0001f)
            {
                var speed = FlySpeed;
                if (ks.IsKeyDown(Keys.LeftControl)) speed *= SprintMultiplier;
                PlayerEntity.Move(a.Normalized() * speed * dt);
            }

            PlayerEntity.PrevPosition = PlayerEntity.Position;
            PlayerEntity.InterpolatedPosition = PlayerEntity.Position;
        }

        private static void UpdateWalking(KeyboardState ks, float dt, WorldBase world)
        {
            var inputX = 0f;
            var inputZ = 0f;
            if (ks.IsKeyDown(Keys.A)) inputX -= 1;
            if (ks.IsKeyDown(Keys.D)) inputX += 1;
            if (ks.IsKeyDown(Keys.S)) inputZ -= 1;
            if (ks.IsKeyDown(Keys.W)) inputZ += 1;

            var forward = new Vector2((float) Math.Sin(PlayerEntity.Yaw), (float) Math.Cos(PlayerEntity.Yaw));
            var wish = PlayerEntity.Right.Xz * inputX + forward * inputZ;
            if (wish.LengthSquared > 1f) wish = wish.Normalized();

            var jump = ks.IsKeyDown(Keys.Space);
            var sprint = ks.IsKeyDown(Keys.LeftControl) && inputZ > 0;

            _physicsAccumulator += dt;
            while (_physicsAccumulator >= PlayerPhysics.TickSeconds)
            {
                PlayerEntity.PrevPosition = PlayerEntity.Position;
                PlayerPhysics.Tick(world, PlayerEntity, wish, jump, sprint);
                _physicsAccumulator -= PlayerPhysics.TickSeconds;
            }

            var alpha = _physicsAccumulator / PlayerPhysics.TickSeconds;
            PlayerEntity.InterpolatedPosition =
                Vector3.Lerp(PlayerEntity.PrevPosition, PlayerEntity.Position, alpha);
        }

        private static void BreakBlock(WorldBase world)
        {
            if (_blockRaytrace == null) return;
            world.SetBlock(_blockRaytrace.BlockPos, BlockRegistry.BlockAir);
        }

        private static void PlaceBlock(WorldBase world)
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

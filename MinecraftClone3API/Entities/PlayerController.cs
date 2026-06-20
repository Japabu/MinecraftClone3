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
        private const float DoubleTapSeconds = 0.3f;

        private static BlockRaytraceResult _blockRaytrace;
        private static string _currentBlock = "Vanilla:Stone";
        private static bool _skipMouseDelta;
        private static readonly Stopwatch _spaceTapTimer = Stopwatch.StartNew();

        public static void SetEntity(EntityPlayer playerEntity)
        {
            PlayerEntity = playerEntity;
            Camera.ParentEntity = playerEntity;
        }

        /// <summary>Per-display-frame: look, the fly toggle, hotbar, debug keys, break/place, and the camera.
        /// Movement is a fixed 20 tps step in <see cref="Tick"/>; this runs every frame for responsiveness
        /// and reads the interpolated position (set by <see cref="ApplyInterpolation"/> before it is called).</summary>
        public static void UpdateFrame(GameWindow window, WorldBase world)
        {
            _blockRaytrace = world.BlockRaytrace(PlayerEntity.RenderPosition + PlayerEntity.EyeOffset,
                PlayerEntity.Forward, 8);

            if (!window.IsFocused) return;

            var ks = window.KeyboardState;
            var ms = window.MouseState;

            var delta = ms.Delta;
            if (_skipMouseDelta)
            {
                delta = Vector2.Zero;
                _skipMouseDelta = false;
            }
            var sensitivity = GraphicsSettings.MouseSensitivity;
            PlayerEntity.Rotate(-delta.Y * sensitivity, -delta.X * sensitivity);

            if (ks.IsKeyPressed(Keys.Space))
            {
                if (_spaceTapTimer.Elapsed.TotalSeconds < DoubleTapSeconds)
                {
                    PlayerEntity.Flying = !PlayerEntity.Flying;
                    PlayerEntity.Velocity = Vector3.Zero;
                }
                _spaceTapTimer.Restart();
            }

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
                PlaceBlock(world, ks);

            Camera.Update();
        }

        /// <summary>One fixed 20 tps simulation step (one walk physics step, or one fly move). Records
        /// PrevPosition so <see cref="ApplyInterpolation"/> can smooth the 20 tps motion to the frame rate.</summary>
        public static void Tick(GameWindow window, WorldBase world)
        {
            if (!window.IsFocused) return;

            var ks = window.KeyboardState;
            PlayerEntity.PrevPosition = PlayerEntity.Position;

            if (PlayerEntity.Flying) TickFlying(ks);
            else TickWalking(ks, world);
        }

        public static void ApplyInterpolation(float alpha)
        {
            PlayerEntity.InterpolatedPosition =
                Vector3.Lerp(PlayerEntity.PrevPosition, PlayerEntity.Position, alpha);
        }

        private static void TickFlying(KeyboardState ks)
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
                PlayerEntity.Move(a.Normalized() * speed * PlayerPhysics.TickSeconds);
            }
        }

        private static void TickWalking(KeyboardState ks, WorldBase world)
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

            PlayerPhysics.Tick(world, PlayerEntity, wish, jump, sprint);
        }

        private static void BreakBlock(WorldBase world)
        {
            if (_blockRaytrace == null) return;
            world.SetBlock(_blockRaytrace.BlockPos, BlockRegistry.BlockAir);
        }

        private static void PlaceBlock(WorldBase world, KeyboardState ks)
        {
            if (_blockRaytrace == null) return;
            var block = GameRegistry.GetBlock(_currentBlock);
            world.PlaceBlock(PlayerEntity, _blockRaytrace.BlockPos + _blockRaytrace.Face.GetNormali(), block,
                block.GetPlacementMetadata(ks, PlayerEntity, _blockRaytrace));

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

using System;
using System.Diagnostics;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Items;
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

            if (Keybinds.IsPressed(ks, GameAction.Jump))
            {
                if (_spaceTapTimer.Elapsed.TotalSeconds < DoubleTapSeconds)
                {
                    PlayerEntity.Flying = !PlayerEntity.Flying;
                    PlayerEntity.Velocity = Vector3.Zero;
                }
                _spaceTapTimer.Restart();
            }

            if (world is WorldClient client)
            {
                UpdateHotbarSelection(client, ks, ms);
                if (Keybinds.IsPressed(ks, GameAction.Drop))
                    client.SendDropItem(ks.IsKeyDown(Keys.LeftControl) || ks.IsKeyDown(Keys.RightControl));
            }

            if (ks.IsKeyPressed(Keys.F1)) RenderDebug.ShowControls = !RenderDebug.ShowControls;
            if (ks.IsKeyPressed(Keys.F3)) RenderDebug.ShowDiagnostics = !RenderDebug.ShowDiagnostics;
            if (ks.IsKeyPressed(Keys.F4)) ChunkBorderRenderer.Enabled = !ChunkBorderRenderer.Enabled;
            if (ks.IsKeyPressed(Keys.F7)) RenderDebug.ShadowFactor = !RenderDebug.ShadowFactor;
            if (ks.IsKeyPressed(Keys.F10)) Profiler.Toggle();
            if (ks.IsKeyPressed(Keys.F2)) WorldRenderer.FixedTimeOfDay = WorldRenderer.FixedTimeOfDay == null ? 220.0 : 0;

            if (ms.IsButtonDown(MouseButton.Left) && !ms.WasButtonDown(MouseButton.Left))
                BreakBlock(world);
            if (ms.IsButtonDown(MouseButton.Right) && !ms.WasButtonDown(MouseButton.Right) && !TryActivateBlock(window, world))
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
            if (Keybinds.IsDown(ks, GameAction.Left)) a.X -= 1;
            if (Keybinds.IsDown(ks, GameAction.Right)) a.X += 1;
            if (Keybinds.IsDown(ks, GameAction.Sneak)) a.Y -= 1;
            if (Keybinds.IsDown(ks, GameAction.Jump)) a.Y += 1;
            if (Keybinds.IsDown(ks, GameAction.Back)) a.Z -= 1;
            if (Keybinds.IsDown(ks, GameAction.Forward)) a.Z += 1;
            if (Math.Abs(a.LengthSquared) > 0.0001f)
            {
                var speed = FlySpeed;
                if (Keybinds.IsDown(ks, GameAction.Sprint)) speed *= SprintMultiplier;
                PlayerEntity.Move(a.Normalized() * speed * PlayerPhysics.TickSeconds);
            }
        }

        private static void TickWalking(KeyboardState ks, WorldBase world)
        {
            var inputX = 0f;
            var inputZ = 0f;
            if (Keybinds.IsDown(ks, GameAction.Left)) inputX -= 1;
            if (Keybinds.IsDown(ks, GameAction.Right)) inputX += 1;
            if (Keybinds.IsDown(ks, GameAction.Back)) inputZ -= 1;
            if (Keybinds.IsDown(ks, GameAction.Forward)) inputZ += 1;

            var forward = new Vector2((float)Math.Sin(PlayerEntity.Yaw), (float)Math.Cos(PlayerEntity.Yaw));
            var wish = PlayerEntity.Right.Xz * inputX + forward * inputZ;
            if (wish.LengthSquared > 1f) wish = wish.Normalized();

            var jump = Keybinds.IsDown(ks, GameAction.Jump);
            var sprint = Keybinds.IsDown(ks, GameAction.Sprint) && inputZ > 0;

            PlayerPhysics.Tick(world, PlayerEntity, wish, jump, sprint);
        }

        private static void BreakBlock(WorldBase world)
        {
            if (_blockRaytrace == null) return;
            world.SetBlock(_blockRaytrace.BlockPos, BlockRegistry.BlockAir);
        }

        /// <summary>Right-click interaction: if the player is looking at an interactable block (e.g. a crafting
        /// table) let it handle the click (open its GUI) and report that placement should be suppressed.</summary>
        private static bool TryActivateBlock(GameWindow window, WorldBase world)
        {
            if (_blockRaytrace == null) return false;
            var pos = _blockRaytrace.BlockPos;
            var block = world.GetBlock(pos.X, pos.Y, pos.Z);
            return block != null && block.OnActivated(window, world, pos, PlayerEntity);
        }

        private static void PlaceBlock(WorldBase world, KeyboardState ks)
        {
            if (!(world is WorldClient client)) return;

            var held = client.Inventory.SelectedItem;
            if (held.IsEmpty) return;
            var item = held.Item;
            if (item == null) return;

            // Aiming at an entity with an entity-usable item (shears on a sheep)? That takes precedence and
            // doesn't need a block under the crosshair.
            if (item.UsableOnEntity)
            {
                var entity = PickEntity(client);
                if (entity != null) { client.SendUseItemOnEntity(entity.EntityId); return; }
            }

            if (_blockRaytrace == null) return;
            var target = _blockRaytrace.BlockPos + _blockRaytrace.Face.GetNormali();
            var block = item.GetBlock();
            if (block != null)
            {
                world.PlaceBlock(PlayerEntity, target, block,
                    block.GetPlacementMetadata(ks, PlayerEntity, _blockRaytrace));
                return;
            }

            // Usable non-block items (a spawn egg) ask the server to act; other items do nothing.
            if (item.IsUsable) client.SendUseItem(target);
        }

        // The entity the player is aiming at within reach (and nearer than the targeted block, so you can't
        // reach through a wall), or null. A ray–AABB test against each entity's collision box.
        private static Entity PickEntity(WorldClient client)
        {
            const float reach = 8f;
            var origin = PlayerEntity.RenderPosition + PlayerEntity.EyeOffset;
            var dir = PlayerEntity.Forward;
            var maxDist = reach;
            if (_blockRaytrace != null)
                maxDist = MathF.Min(maxDist,
                    (_blockRaytrace.BlockPos.ToVector3() + new Vector3(0.5f) - origin).Length);

            Entity best = null;
            foreach (var entity in client.Entities.Values)
            {
                if (entity.Type == null) continue;
                var half = entity.Type.Width * 0.5f;
                var min = entity.RenderPosition - new Vector3(half, 0, half);
                var max = entity.RenderPosition + new Vector3(half, entity.Type.Height, half);
                if (RayAabb(origin, dir, min, max, out var dist) && dist <= maxDist)
                {
                    maxDist = dist;
                    best = entity;
                }
            }

            return best;
        }

        private static bool RayAabb(Vector3 origin, Vector3 dir, Vector3 min, Vector3 max, out float t)
        {
            var tmin = 0f;
            var tmax = float.MaxValue;
            t = 0f;
            if (!Slab(origin.X, dir.X, min.X, max.X, ref tmin, ref tmax)) return false;
            if (!Slab(origin.Y, dir.Y, min.Y, max.Y, ref tmin, ref tmax)) return false;
            if (!Slab(origin.Z, dir.Z, min.Z, max.Z, ref tmin, ref tmax)) return false;
            t = tmin;
            return true;
        }

        private static bool Slab(float o, float d, float lo, float hi, ref float tmin, ref float tmax)
        {
            if (MathF.Abs(d) < 1e-6f) return o >= lo && o <= hi;
            var inv = 1f / d;
            var t1 = (lo - o) * inv;
            var t2 = (hi - o) * inv;
            if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; }
            if (t1 > tmin) tmin = t1;
            if (t2 < tmax) tmax = t2;
            return tmin <= tmax;
        }

        /// <summary>Hotbar slot selection: number keys 1-9 jump to a slot, the scroll wheel steps through
        /// them (wrapping). A change is mirrored to the server so the held item stays authoritative.</summary>
        private static void UpdateHotbarSelection(WorldClient client, KeyboardState ks, MouseState ms)
        {
            var selected = client.Inventory.SelectedHotbar;

            for (var i = 0; i < Inventory.HotbarSize; i++)
                if (ks.IsKeyPressed(Keys.D1 + i)) selected = i;

            var scroll = ms.ScrollDelta.Y;
            if (scroll > 0) selected = (selected + Inventory.HotbarSize - 1) % Inventory.HotbarSize;
            else if (scroll < 0) selected = (selected + 1) % Inventory.HotbarSize;

            if (selected == client.Inventory.SelectedHotbar) return;
            client.Inventory.SelectedHotbar = selected;
            client.SendHeldSlot(selected);
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

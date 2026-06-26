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

        // Accumulated downward travel (blocks) since leaving the ground; reported to the server on landing so it
        // can apply fall damage (the player is position-authoritative, so the client measures the fall).
        private static float _fallDistance;

        // Survival block-breaking: the block currently being mined and 0..1 progress toward breaking it, plus a
        // wall-clock timer so mining accrues at the display rate (UpdateFrame runs per frame, not per tick).
        private static Vector3i? _miningTarget;
        private static float _miningProgress;
        private static readonly Stopwatch _frameTimer = Stopwatch.StartNew();

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

            if (PlayerEntity.GameMode == GameMode.Creative && Keybinds.IsPressed(ks, GameAction.Jump))
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

            HandleBreaking(world, ms);
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

            TrackFall(world);
        }

        /// <summary>Accumulates downward travel while airborne and reports the total fall to the server on
        /// landing (the server applies the damage). Flying resets the accumulator so descending in fly-mode and
        /// disabling it never deals damage.</summary>
        private static void TrackFall(WorldBase world)
        {
            if (PlayerEntity.Flying)
            {
                _fallDistance = 0f;
                return;
            }

            if (PlayerEntity.OnGround)
            {
                if (_fallDistance > 0f && world is WorldClient client) client.SendFall(_fallDistance);
                _fallDistance = 0f;
                return;
            }

            var dy = PlayerEntity.Position.Y - PlayerEntity.PrevPosition.Y;
            if (dy < 0f) _fallDistance += -dy;
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

        /// <summary>Creative breaks instantly on click; survival holds left-click to mine, accruing progress on
        /// the targeted block at the Minecraft mining rate (tool match and tier vs. block hardness) and breaking
        /// it when full. Progress resets if the target changes or the button is released.</summary>
        private static void HandleBreaking(WorldBase world, MouseState ms)
        {
            var dt = (float) _frameTimer.Elapsed.TotalSeconds;
            _frameTimer.Restart();
            // Cap the step so a long stall (alt-tab, an open menu) can't dump a full break into one frame.
            if (dt > 0.25f) dt = 0.25f;

            var leftDown = ms.IsButtonDown(MouseButton.Left);

            if (PlayerEntity.GameMode == GameMode.Creative)
            {
                if (leftDown && !ms.WasButtonDown(MouseButton.Left)) BreakBlock(world);
                _miningTarget = null;
                _miningProgress = 0f;
                return;
            }

            if (!leftDown || _blockRaytrace == null)
            {
                _miningTarget = null;
                _miningProgress = 0f;
                return;
            }

            var target = _blockRaytrace.BlockPos;
            if (_miningTarget != target)
            {
                _miningTarget = target;
                _miningProgress = 0f;
            }

            Item tool = null;
            if (world is WorldClient client)
            {
                var held = client.Inventory.SelectedItem;
                if (!held.IsEmpty) tool = held.Item;
            }

            var breakSeconds = BreakSeconds(_blockRaytrace.Block, tool);
            if (breakSeconds < 0f) { _miningProgress = 0f; return; }   // unbreakable (bedrock)

            if (breakSeconds <= 0f) _miningProgress = 1f;
            else _miningProgress += dt / breakSeconds;

            if (_miningProgress >= 1f)
            {
                world.SetBlock(target, BlockRegistry.BlockAir);
                _miningTarget = null;
                _miningProgress = 0f;
            }
        }

        /// <summary>Seconds to break <paramref name="block"/> with the held <paramref name="tool"/>, following
        /// Minecraft's mining formula: the tool's <see cref="Item.MiningSpeed"/> applies only when its
        /// <see cref="Item.ToolType"/> matches the block's <see cref="Block.PreferredTool"/>, and the rate is
        /// throttled (÷100 vs ÷30) when the block <see cref="Block.RequiresCorrectTool"/> but the tool is wrong
        /// or too low a tier. Returns 0 for instant blocks and -1 for unbreakable ones.</summary>
        private static float BreakSeconds(Block block, Item tool)
        {
            var hardness = block.Hardness;
            if (hardness < 0f) return -1f;
            if (hardness == 0f) return 0f;

            var matches = tool != null && tool.ToolType != ToolType.None && tool.ToolType == block.PreferredTool;
            var speed = matches ? tool.MiningSpeed : 1f;
            var correctForDrops = !block.RequiresCorrectTool || (matches && tool.ToolTier >= block.RequiredToolTier);
            var divider = correctForDrops ? 30f : 100f;

            var progressPerTick = speed / hardness / divider;
            return PlayerPhysics.TickSeconds / progressPerTick;
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
            if (_blockRaytrace == null) return;
            if (!(world is WorldClient client)) return;

            var held = client.Inventory.SelectedItem;
            if (held.IsEmpty) return;
            var item = held.Item;
            if (item == null) return;

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

            // The breaking crack overlay, while mining the targeted block in survival.
            if (_miningProgress > 0f && _miningTarget == _blockRaytrace.BlockPos)
                BlockBreakRenderer.Render(_blockRaytrace.BoundingBox, _blockRaytrace.BlockPos.ToVector3(),
                    _miningProgress, camera, projection);
        }
    }
}

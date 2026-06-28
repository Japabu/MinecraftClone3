using System;
using System.Diagnostics;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Graphics.Rhi;
using MinecraftClone3API.IO;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Input;

namespace MinecraftClone3API.Entities
{
    public static class PlayerController
    {
        public static EntityPlayer PlayerEntity;
        public static Camera Camera = new Camera();

        private const float FlySpeed = 16f;
        private const float SprintMultiplier = 3f;
        private const float DoubleTapSeconds = 0.3f;
        // How far the third-person camera trails the eye, and the gap it keeps when a block clips the view.
        private const float ThirdPersonDistance = 4f;
        private const float ThirdPersonClearance = 0.2f;

        /// <summary>The F5 camera cycle: first-person, third-person behind, third-person facing the player.</summary>
        public enum CameraPerspective { FirstPerson, ThirdBack, ThirdFront }

        public static CameraPerspective Perspective = CameraPerspective.FirstPerson;

        /// <summary>True when the local player's own body should be drawn (any third-person view).</summary>
        public static bool RenderSelf => Perspective != CameraPerspective.FirstPerson;

        // Sprinting (survival + creative walk): started by holding the Sprint key or double-tapping Forward
        // while moving forward, and (in survival) only while not too hungry. Sticky once started — it keeps
        // going on held Forward alone — until Forward is released, hunger runs low, or a wall is hit. Boosts
        // walk speed (PlayerPhysics.SprintMultiplier) and widens the FOV like Minecraft.
        private const float SprintFovScale = 1.15f;
        private const float ForwardDoubleTapSeconds = 0.3f;
        private const float SprintHungerThreshold = 6f;
        private static bool _sprinting;
        private static bool _horizontalCollision;
        private static readonly Stopwatch _forwardTapTimer = Stopwatch.StartNew();
        // Eased multiplier on the configured FOV: 1 normally, easing toward SprintFovScale while sprinting.
        public static float FovScale = 1f;
        private static readonly Stopwatch _fovTimer = Stopwatch.StartNew();

        private static BlockRaytraceResult _blockRaytrace;
        private static bool _skipMouseDelta;
        private static readonly Stopwatch _spaceTapTimer = Stopwatch.StartNew();
        // Wall-clock between frames, for advancing the local player's walk cycle (it isn't in the entity
        // interpolation set, so nothing else drives its limb swing for the third-person view).
        private static readonly Stopwatch _walkTimer = Stopwatch.StartNew();

        // Accumulated downward travel (blocks) since leaving the ground; reported to the server on landing so it
        // can apply fall damage (the player is position-authoritative, so the client measures the fall).
        private static float _fallDistance;

        // Survival block-breaking: the block currently being mined and 0..1 progress toward breaking it, plus a
        // wall-clock timer so mining accrues at the display rate (UpdateFrame runs per frame, not per tick).
        private static Vector3i? _miningTarget;
        private static float _miningProgress;
        // True while a left-click that landed on a creature (an attack) is still held, so the same hold doesn't
        // also break/mine. Cleared on release.
        private static bool _attacking;
        private static readonly Stopwatch _frameTimer = Stopwatch.StartNew();

        // The world being driven, cached each frame so the discrete input-event handlers
        // (OnKeyDown/OnMouseDown/OnScroll, routed from StateWorld) act without it being threaded through.
        private static WorldBase _world;

        // First-person swing: a one-shot 0..1 arc (re)started by Swing() on attack/mine/place/use and advanced
        // each frame; the held-item viewmodel maps it to the arm/item arc. Mining re-triggers it so it loops.
        private const float SwingSeconds = 0.25f;
        private static readonly Stopwatch _swingTimer = Stopwatch.StartNew();
        private static bool _swinging;
        private static float _swingProgress;

        /// <summary>The current swing arc position (0..1), or 0 when no swing is in progress.</summary>
        public static float SwingPhase => _swinging ? _swingProgress : 0f;

        /// <summary>(Re)starts the first-person swing animation (a melee/mine/place gesture).</summary>
        public static void Swing()
        {
            _swinging = true;
            _swingProgress = 0f;
        }

        private static void AdvanceSwing()
        {
            var dt = (float) _swingTimer.Elapsed.TotalSeconds;
            _swingTimer.Restart();
            if (!_swinging) return;
            if (dt > 0.25f) dt = 0.25f;
            _swingProgress += dt / SwingSeconds;
            if (_swingProgress >= 1f) { _swinging = false; _swingProgress = 0f; }
        }

        public static void SetEntity(EntityPlayer playerEntity)
        {
            PlayerEntity = playerEntity;
            Camera.ParentEntity = playerEntity;
        }

        /// <summary>Per-display-frame: look, the fly toggle, hotbar, debug keys, break/place, and the camera.
        /// Movement is a fixed 20 tps step in <see cref="Tick"/>; this runs every frame for responsiveness
        /// and reads the interpolated position (set by <see cref="ApplyInterpolation"/> before it is called).</summary>
        public static void UpdateFrame(WorldBase world, bool focused)
        {
            _world = world;
            _blockRaytrace = world.BlockRaytrace(PlayerEntity.RenderPosition + PlayerEntity.EyeOffset,
                PlayerEntity.Forward, 8);

            AdvanceSwing();

            if (!focused) return;

            // Mouse-look from the accumulated frame delta (Silk MouseMove); discrete actions arrive via the
            // OnKeyDown/OnMouseDown/OnScroll handlers below, routed from StateWorld when it is the foreground.
            var delta = ClientResources.Input.ConsumeMouseDelta();
            if (_skipMouseDelta)
            {
                delta = Vector2.Zero;
                _skipMouseDelta = false;
            }
            var sensitivity = GraphicsSettings.MouseSensitivity;
            PlayerEntity.Rotate(-delta.Y * sensitivity, -delta.X * sensitivity);

            HandleMiningHold(world);
            UpdateCamera(world);
        }

        /// <summary>Discrete key press (routed from <c>StateWorld.OnKeyDown</c> when gameplay is the foreground
        /// layer): debug toggles, the creative fly double-tap, drop, and hotbar number keys.</summary>
        public static void OnKeyDown(Key key)
        {
            if (key == Key.F1) RenderDebug.ShowControls = !RenderDebug.ShowControls;
            else if (key == Key.F3) RenderDebug.ShowDiagnostics = !RenderDebug.ShowDiagnostics;
            else if (key == Key.F4) ChunkBorderRenderer.Enabled = !ChunkBorderRenderer.Enabled;
            else if (key == Key.F7) RenderDebug.ShadowFactor = !RenderDebug.ShadowFactor;
            else if (key == Key.F10) Profiler.Toggle();
            else if (key == Key.F2) WorldRenderer.FixedTimeOfDay = WorldRenderer.FixedTimeOfDay == null ? 220.0 : 0;
            else if (key == Key.F5) Perspective = (CameraPerspective) (((int) Perspective + 1) % 3);

            if (PlayerEntity == null) return;

            if (PlayerEntity.GameMode == GameMode.Creative && Keybinds.Matches(GameAction.Jump, key))
            {
                if (_spaceTapTimer.Elapsed.TotalSeconds < DoubleTapSeconds)
                {
                    PlayerEntity.Flying = !PlayerEntity.Flying;
                    PlayerEntity.Velocity = Vector3.Zero;
                }
                _spaceTapTimer.Restart();
            }

            // Forward double-tap engages sprint (the held-key path is handled per-tick in UpdateSprint).
            if (Keybinds.Matches(GameAction.Forward, key))
            {
                if (_forwardTapTimer.Elapsed.TotalSeconds < ForwardDoubleTapSeconds && CanSprint())
                    _sprinting = true;
                _forwardTapTimer.Restart();
            }

            if (_world is WorldClient client)
            {
                if (Keybinds.Matches(GameAction.Drop, key))
                    client.SendDropItem(ClientResources.Input.IsKeyDown(Key.ControlLeft) ||
                                        ClientResources.Input.IsKeyDown(Key.ControlRight));
                if (key >= Key.Number1 && key <= Key.Number9)
                    SetHotbar(client, key - Key.Number1);
            }
        }

        /// <summary>Discrete mouse press: left = attack-or-break (creative breaks instantly; survival starts a
        /// mining hold), right = activate-or-place.</summary>
        public static void OnMouseDown(MouseButton button)
        {
            if (_world == null) return;
            if (button == MouseButton.Left) AttackOrBreak(_world);
            else if (button == MouseButton.Right && !TryActivateBlock(_world)) PlaceBlock(_world);
        }

        public static void OnMouseUp(MouseButton button)
        {
            if (button != MouseButton.Left) return;
            _attacking = false;
            _miningTarget = null;
            _miningProgress = 0f;
        }

        /// <summary>Hotbar scroll-wheel step (routed from <c>StateWorld.OnScroll</c>).</summary>
        public static void OnScroll(float delta)
        {
            if (!(_world is WorldClient client)) return;
            var selected = client.Inventory.SelectedHotbar;
            if (delta > 0) selected = (selected + Inventory.HotbarSize - 1) % Inventory.HotbarSize;
            else if (delta < 0) selected = (selected + 1) % Inventory.HotbarSize;
            SetHotbar(client, selected);
        }

        /// <summary>Drives the camera from the player each frame. First-person follows the eye directly; the
        /// third-person views trail (or face) the eye, pulled in if a block would clip the line of sight. The
        /// local player's walk cycle is advanced here too so its limbs swing when its body is visible.</summary>
        private static void UpdateCamera(WorldBase world)
        {
            var walkDt = (float) _walkTimer.Elapsed.TotalSeconds;
            _walkTimer.Restart();
            var moved = PlayerEntity.Position - PlayerEntity.PrevPosition;
            var speed = new Vector2(moved.X, moved.Z).Length / PlayerPhysics.TickSeconds;
            PlayerEntity.AdvanceWalkCycle(speed, walkDt);

            if (Perspective == CameraPerspective.FirstPerson)
            {
                Camera.Update();
                return;
            }

            var eye = PlayerEntity.RenderPosition + PlayerEntity.EyeOffset;
            var lookForward = Perspective == CameraPerspective.ThirdFront ? -PlayerEntity.Forward : PlayerEntity.Forward;
            var distance = ThirdPersonDistance;
            var hit = world.BlockRaytrace(eye, -lookForward, ThirdPersonDistance);
            if (hit != null) distance = MathF.Max(0f, hit.Distance - ThirdPersonClearance);
            Camera.UpdateThirdPerson(eye, lookForward, distance);
        }

        /// <summary>One fixed 20 tps simulation step (one walk physics step, or one fly move). Records
        /// PrevPosition so <see cref="ApplyInterpolation"/> can smooth the 20 tps motion to the frame rate.</summary>
        public static void Tick(WorldBase world, bool focused)
        {
            _world = world;
            if (!focused) return;

            PlayerEntity.PrevPosition = PlayerEntity.Position;

            if (PlayerEntity.Flying) TickFlying();
            else TickWalking(world);

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

        /// <summary>Clears the accumulated fall distance — used after a server teleport (an ender pearl) so the
        /// jump in position isn't billed as a fall on the next landing.</summary>
        public static void ResetFall() => _fallDistance = 0f;

        public static void ApplyInterpolation(float alpha)
        {
            PlayerEntity.InterpolatedPosition =
                Vector3D.Lerp(PlayerEntity.PrevPosition, PlayerEntity.Position, alpha);
        }

        private static void TickFlying()
        {
            var a = Vector3.Zero;
            if (Keybinds.IsDown(GameAction.Left)) a.X -= 1;
            if (Keybinds.IsDown(GameAction.Right)) a.X += 1;
            if (Keybinds.IsDown(GameAction.Sneak)) a.Y -= 1;
            if (Keybinds.IsDown(GameAction.Jump)) a.Y += 1;
            if (Keybinds.IsDown(GameAction.Back)) a.Z -= 1;
            if (Keybinds.IsDown(GameAction.Forward)) a.Z += 1;
            if (Math.Abs(a.LengthSquared) > 0.0001f)
            {
                var speed = FlySpeed;
                if (Keybinds.IsDown(GameAction.Sprint)) speed *= SprintMultiplier;
                PlayerEntity.Move(Vector3D.Normalize(a) * speed * PlayerPhysics.TickSeconds);
            }
        }

        private static void TickWalking(WorldBase world)
        {
            var inputX = 0f;
            var inputZ = 0f;
            if (Keybinds.IsDown(GameAction.Left)) inputX -= 1;
            if (Keybinds.IsDown(GameAction.Right)) inputX += 1;
            if (Keybinds.IsDown(GameAction.Back)) inputZ -= 1;
            if (Keybinds.IsDown(GameAction.Forward)) inputZ += 1;

            var forward = new Vector2((float)Math.Sin(PlayerEntity.Yaw), (float)Math.Cos(PlayerEntity.Yaw));
            var right = PlayerEntity.Right;
            var wish = new Vector2(right.X, right.Z) * inputX + forward * inputZ;
            if (wish.LengthSquared > 1f) wish = Vector2D.Normalize(wish);

            UpdateSprint();
            var jump = Keybinds.IsDown(GameAction.Jump);

            _horizontalCollision = PlayerPhysics.Tick(world, PlayerEntity, wish, jump, _sprinting);
        }

        /// <summary>Sprint state machine (Minecraft-style). Engages on a held Sprint key or a Forward
        /// double-tap while pushing forward and (in survival) not too hungry; stays engaged on held Forward
        /// alone until Forward is released, hunger drops, or a wall is hit. Also eases the sprint FOV. Idle
        /// while flying — creative free-flight has its own fast modifier.</summary>
        private static void UpdateSprint()
        {
            var forwardDown = Keybinds.IsDown(GameAction.Forward);

            if (Keybinds.IsDown(GameAction.Sprint) && forwardDown && CanSprint())
                _sprinting = true;

            if (!forwardDown || !CanSprint() || _horizontalCollision || PlayerEntity.Flying)
                _sprinting = false;

            var dt = (float) _fovTimer.Elapsed.TotalSeconds;
            _fovTimer.Restart();
            var target = _sprinting ? SprintFovScale : 1f;
            FovScale += (target - FovScale) * MathF.Min(1f, dt * 12f);
        }

        // Survival can't sprint while too hungry (Minecraft: hunger must be above 6); creative always can.
        private static bool CanSprint()
            => PlayerEntity.GameMode != GameMode.Survival || PlayerEntity.Hunger > SprintHungerThreshold;

        private static void BreakBlock(WorldBase world)
        {
            if (_blockRaytrace == null) return;
            world.SetBlock(_blockRaytrace.BlockPos, BlockRegistry.BlockAir);
        }

        /// <summary>A fresh left-press: a melee attack on a creature in reach (swallows the rest of the hold),
        /// else a creative instant-break. Survival mining accrues per-frame in <see cref="HandleMiningHold"/>
        /// while the button stays held.</summary>
        private static void AttackOrBreak(WorldBase world)
        {
            if (world is WorldClient attackClient)
            {
                var entity = PickEntity(attackClient);
                if (entity != null && entity.Type != null && entity.Type.Kind == EntityKind.Creature)
                {
                    attackClient.SendAttackEntity(entity.EntityId);
                    _attacking = true;
                    Swing();
                    return;
                }
            }
            if (PlayerEntity.GameMode == GameMode.Creative) { BreakBlock(world); Swing(); }
        }

        /// <summary>Survival mining while left-click is held: accrues progress on the targeted block at the
        /// Minecraft mining rate (tool match and tier vs. block hardness) and breaks it when full. Progress
        /// resets if the target changes, the button is released, or the hold was claimed by an attack.</summary>
        private static void HandleMiningHold(WorldBase world)
        {
            var dt = (float) _frameTimer.Elapsed.TotalSeconds;
            _frameTimer.Restart();
            // Cap the step so a long stall (alt-tab, an open menu) can't dump a full break into one frame.
            if (dt > 0.25f) dt = 0.25f;

            var leftDown = ClientResources.Input.IsMouseDown(MouseButton.Left);
            if (!leftDown || _attacking || PlayerEntity.GameMode == GameMode.Creative || _blockRaytrace == null)
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

            // Loop the swing while actively mining.
            if (!_swinging) Swing();

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
        private static bool TryActivateBlock(WorldBase world)
        {
            if (_blockRaytrace == null) return false;
            var pos = _blockRaytrace.BlockPos;
            var block = world.GetBlock(pos.X, pos.Y, pos.Z);
            return block != null && block.OnActivated(world, pos, PlayerEntity);
        }

        private static void PlaceBlock(WorldBase world)
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
                if (entity != null) { client.SendUseItemOnEntity(entity.EntityId); Swing(); return; }
            }

            var block = item.GetBlock();
            if (block != null)
            {
                if (_blockRaytrace == null) return;
                var target = _blockRaytrace.BlockPos + _blockRaytrace.Face.GetNormali();
                world.PlaceBlock(PlayerEntity, target, block,
                    block.GetPlacementMetadata(PlayerEntity, _blockRaytrace));
                Swing();
                return;
            }

            // Usable non-block items ask the server to act. A spawn egg needs a targeted cell; a thrown item
            // (ender pearl) doesn't (RequiresBlockTarget false), so it fires even aiming at the sky.
            if (!item.IsUsable) return;
            if (_blockRaytrace != null)
                client.SendUseItem(_blockRaytrace.BlockPos + _blockRaytrace.Face.GetNormali());
            else if (item.RequiresBlockTarget)
                return;
            else
                client.SendUseItem(Vector3i.Zero);
            Swing();
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

        /// <summary>Select a hotbar slot (a number key or a scroll step), wrapping into range and mirroring the
        /// change to the server so the held item stays authoritative.</summary>
        private static void SetHotbar(WorldClient client, int slot)
        {
            slot = ((slot % Inventory.HotbarSize) + Inventory.HotbarSize) % Inventory.HotbarSize;
            if (slot == client.Inventory.SelectedHotbar) return;
            client.Inventory.SelectedHotbar = slot;
            client.SendHeldSlot(slot);
        }

        /// <summary>Discards the next mouse delta so re-grabbing the cursor (window refocus,
        /// closing a menu) doesn't snap the camera.</summary>
        public static void ResetMouse() => _skipMouseDelta = true;

        public static void Render(RenderPass pass, Camera camera)
        {
            if (_blockRaytrace == null) return;

            BoundingBoxRenderer.Render(pass, _blockRaytrace.BoundingBox, _blockRaytrace.BlockPos.ToVector3(), 1.01f);

            // The breaking crack overlay, while mining the targeted block in survival.
            if (_miningProgress > 0f && _miningTarget == _blockRaytrace.BlockPos)
                BlockBreakRenderer.Render(pass, _blockRaytrace.BoundingBox, _blockRaytrace.BlockPos.ToVector3(),
                    _miningProgress);
        }
    }
}

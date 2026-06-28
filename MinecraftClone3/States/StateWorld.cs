using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Client.GUI;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Client.StateSystem;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Graphics.Rhi;
using MinecraftClone3API.IO;
using MinecraftClone3API.Networking;
using MinecraftClone3API.Util;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace MinecraftClone3.States
{
    internal class StateWorld : StateBase
    {
        private const string ServerAddress = "127.0.0.1";

        // Placeholder until the seed-derived spawn arrives in LoginAccept; the loading gate snaps the
        // player onto it once the surrounding chunks have streamed in (see UpdateLoading).
        private static readonly Vector3D<float> SpawnPos = new Vector3D<float>(0, 80, 0);

        private const double LoadingTimeoutSeconds = 30;

        // Bound the main-thread MP connect so an unreachable host fails fast to the menu instead of hanging on
        // the OS default connect timeout (tens of seconds).
        private const double ConnectTimeoutSeconds = 5;

        private readonly bool _multiplayer;
        private readonly bool _benchmark;

        private readonly EntityPlayer _player;
        private readonly WorldClient _world;

        // Singleplayer only: the in-process server the loopback client talks to.
        private readonly WorldServer _integratedServer;
        private readonly ServerNetwork _network;

        private readonly bool _connectionFailed;

        // The previous world's async teardown (see Exit), if it is still saving. A newly opened world and the
        // app-exit path join it first so a save never races the next world on disk or gets cut off at exit.
        private static Thread _pendingTeardown;

        private bool _loading = true;
        private bool _spawnApplied;
        private GuiDeathScreen _deathScreen;
        private int _lastRenderDistanceChunks = -1;
        private float _lastLodHorizonQuality = -1f;
        private int _lastLodHorizonChunks = -1;
        private readonly Stopwatch _loadingTimer = Stopwatch.StartNew();
        private Texture _loadingBackground;
        private Texture _loadingProgressBar;
        private Texture _loadingProgressBarFull;
        // Smoothed loading progress (0..1) so the bar eases toward its target instead of snapping.
        private float _loadProgress;

        private readonly Stopwatch _phaseTimer = new Stopwatch();

        // The whole simulation (player physics, server tick, network pump, send-move) runs at a fixed
        // 20 tps off this accumulator, while input/look/camera/render keep running every display frame and
        // interpolate. _simTimer measures real time between Update calls (the loop is the display rate).
        private readonly Stopwatch _simTimer = new Stopwatch();
        private double _simAccumulator;
        private const double MaxFrameTime = 0.25;
        private const int MaxCatchUpTicks = 5;

        // Nether-portal screen tint, 0..1. Rises 1/80 per tick while the player stands in a portal block (so a
        // survival player's 4 s soak fills the screen as it counts down), decays 1/20 per tick once they step
        // off. Client-local and cosmetic — the authoritative transfer stays server-side.
        private float _timeInPortal;

        /// <summary>Singleplayer: runs the given world in an in-process server over a loopback connection.</summary>
        public StateWorld(WorldInfo world) : this(false, world, false) { }

        /// <summary>Benchmark: singleplayer world driven by the automated <see cref="Benchmark"/> flythrough.</summary>
        public StateWorld(WorldInfo world, bool benchmark) : this(false, world, benchmark) { }

        /// <summary>Multiplayer: connects to the dedicated server over TCP.</summary>
        public StateWorld(bool multiplayer = false) : this(multiplayer, null, false) { }

        private StateWorld(bool multiplayer, WorldInfo world, bool benchmark)
        {
            _multiplayer = multiplayer;
            _benchmark = benchmark;

            // Don't open a world while the previous one is still saving on its teardown thread (Exit) — it would
            // race the same region files on disk. In practice the save is long done by the time the menu is navigated.
            WaitForPendingTeardown();

            // Grab the cursor so relative mouse movement drives the camera (FPS-style). The benchmark drives the
            // camera itself and runs unattended, so it must NOT grab the cursor (that would trap the user's mouse).
            ClientResources.Input.CursorMode = benchmark ? CursorMode.Hidden : CursorMode.Raw;
            PlayerController.ResetMouse();

            _player = new EntityPlayer {Position = SpawnPos};
            PlayerController.SetEntity(_player);

            IConnection connection;
            if (multiplayer)
            {
                try
                {
                    var tcp = new TcpClient();
                    if (!tcp.ConnectAsync(ServerAddress, ServerNetwork.DefaultPort)
                            .Wait(TimeSpan.FromSeconds(ConnectTimeoutSeconds)))
                        throw new TimeoutException($"Connection timed out after {ConnectTimeoutSeconds:0}s");
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
            _world.Login(_multiplayer ? PlayerSettings.Name : "Player");

            ClientProfiling.World = _world;
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
            _lastLodHorizonQuality = GraphicsSettings.LodHorizonQuality;
            _lastLodHorizonChunks = GraphicsSettings.LodHorizonChunks;
            _world.ForceLodMeshRescan();   // a render-distance / horizon change shifts the LOD stride rings

            // Multiplayer: the client can only LOD what the remote server streams, so leave the LOD horizon at
            // the (dormant) default and just drive the client draw distance via RenderDistance.
            if (_integratedServer == null) return;

            _network.ViewDistance = chunks * Chunk.Size;
            _integratedServer.TerrainRadius = chunks + 1;
            _world.CacheDistance = chunks * Chunk.Size + WorldClient.CacheHysteresis;

            // Phase-2 LOD horizon: full detail to `chunks`, then cheap LOD columns out to chunks + LodRingChunks.
            var lodChunks = chunks + LodRingChunks;
            _integratedServer.LodRadius = lodChunks;                         // server gen ring (chunks)
            _network.LodRadius = lodChunks * Chunk.Size;                     // server stream cull (blocks)
            _world.LodRenderDistance = lodChunks * Chunk.Size;              // client draw cull (blocks)
            _world.LodCacheDistance = lodChunks * Chunk.Size + LodColumn.RegionBlocks;
        }

        /// <summary>Chunks of cheap LOD horizon streamed BEYOND the full-detail render distance (the Phase-2
        /// "distant horizon"), driven live by the LOD Horizon graphics slider (0 = off). Default 16 ⇒ a
        /// ~32-chunk / 512-block total horizon at RD 16. The geometry pass is primitive-setup-bound, so this is
        /// the FPS knob; distance-based stride rings / greedy skirts are the deferred way to push it further
        /// without the cost — see CLAUDE.md.</summary>
        private int LodRingChunks => GraphicsSettings.LodHorizonChunks;

        public override void Update(bool focused)
        {
            if (_connectionFailed)
            {
                StateEngine.ReplaceState(new GuiMainMenu());
                return;
            }

            if (_loading)
            {
                UpdateLoading();
                return;
            }

            // Re-apply the radius chain on a render-distance / LOD-horizon change (ApplyRenderDistance also
            // re-steps the LOD rings). A LOD-Quality change just re-steps the existing LOD regions (no chunk
            // remesh — chunks are always full detail now). All main-thread (touches RenderList). Per-frame gate
            // debounces a slider drag.
            if (GraphicsSettings.RenderDistanceChunks != _lastRenderDistanceChunks
                || GraphicsSettings.LodHorizonChunks != _lastLodHorizonChunks)
                ApplyRenderDistance();
            if (GraphicsSettings.LodHorizonQuality != _lastLodHorizonQuality)
            {
                _lastLodHorizonQuality = GraphicsSettings.LodHorizonQuality;
                _world.ForceLodMeshRescan();
            }

            // Mirror the server-authoritative game mode onto the local player so the controller can gate
            // flight, and force-disable flight in survival.
            _player.GameMode = _world.GameMode;
            if (_world.GameMode == GameMode.Survival) _player.Flying = false;
            UpdateDeathScreen();
            UpdateTeleport();

            // The automated benchmark drives the camera itself (no player input / no pause overlay). Esc (pause)
            // and the inventory key are handled in OnKeyDown — opening an overlay makes this state no longer the
            // foreground layer, so StateEngine stops routing input to it.
            var active = focused && !_benchmark;

            // The world keeps simulating whenever it isn't explicitly paused: only the singleplayer Esc menu
            // pauses it (StateEngine.WorldPaused), so a container/inventory screen leaves furnaces smelting and
            // mobs moving, exactly as vanilla — and the non-pausing death overlay keeps the integrated server
            // ticking so a respawn request is processed. Multiplayer can't pause a shared server, and the
            // benchmark always pumps so chunks keep streaming with the window unfocused.
            var simulate = _multiplayer || _benchmark || !StateEngine.WorldPaused;
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
                // Automated mode drives the camera each frame (overwrites the interpolation above): the
                // benchmark flies a path, the inspector parks at fixed A/B poses.
                if (Inspect.Enabled) Inspect.DriveCamera(_player);
                else Benchmark.DriveCamera(_player, _world);
                PlayerController.Camera.Update();
            }
            else if (active) PlayerController.UpdateFrame(_world, true);

            var c = GC.GetAllocatedBytesForCurrentThread();
            _phaseTimer.Restart();
            _world.Update();
            Profiler.AddClientAlloc(GC.GetAllocatedBytesForCurrentThread() - c);
            Profiler.AddClientTime(_phaseTimer.Elapsed.TotalMilliseconds);

            // A portal transfer cleared the cached world and the new spawn is streaming: drop back into the
            // loading screen until the destination chunks arrive (same gate as the initial join).
            if (_world.ConsumeDimensionChange())
            {
                _loading = true;
                _spawnApplied = false;
                _loadProgress = 0f;
                _timeInPortal = 0f;
                _loadingTimer.Restart();
            }
        }

        // StateEngine routes input here only while gameplay is the foreground layer (no overlay open), so opening
        // the pause/inventory overlay below stops further gameplay input without an explicit "active" flag. The
        // benchmark drives itself and the loading gate has no player, so both swallow input entirely.

        public override void OnKeyDown(Key key)
        {
            if (_loading || _connectionFailed || _benchmark) return;

            if (key == Key.Escape)
            {
                StateEngine.AddOverlay(new GuiPauseMenu(_world));
                return;
            }
            if (Keybinds.Matches(GameAction.Inventory, key))
            {
                StateEngine.AddOverlay(_world.GameMode == GameMode.Survival
                    ? (GuiBase) new GuiInventory(_world)
                    : new GuiCreativeInventory(_world));
                return;
            }
            PlayerController.OnKeyDown(key);
        }

        public override void OnMouseDown(MouseButton button, Vector2D<float> guiPos)
        {
            if (_loading || _connectionFailed || _benchmark) return;
            PlayerController.OnMouseDown(button);
        }

        public override void OnMouseUp(MouseButton button, Vector2D<float> guiPos)
        {
            if (_loading || _connectionFailed || _benchmark) return;
            PlayerController.OnMouseUp(button);
        }

        public override void OnScroll(float delta)
        {
            if (_loading || _connectionFailed || _benchmark) return;
            PlayerController.OnScroll(delta);
        }

        /// <summary>Opens the death overlay when the server reports the player died, and closes it (snapping the
        /// player to the server's respawn point — the client is position-authoritative) once the server confirms
        /// the revive. Driven by the death flag in the player stats packet.</summary>
        private void UpdateDeathScreen()
        {
            if (_world.PlayerDead && _deathScreen == null)
            {
                _deathScreen = new GuiDeathScreen(_world);
                StateEngine.AddOverlay(_deathScreen);
            }
            else if (!_world.PlayerDead && _deathScreen != null)
            {
                _deathScreen.IsDead = true;
                _deathScreen = null;

                _player.Position = _world.SpawnPosition;
                _player.PrevPosition = _world.SpawnPosition;
                _player.InterpolatedPosition = _world.SpawnPosition;
                _player.Velocity = Vector3D<float>.Zero;

                ClientResources.Input.CursorMode = CursorMode.Raw;
                PlayerController.ResetMouse();
                PlayerController.Camera.Update();
            }
        }

        /// <summary>Applies a server-commanded teleport (a landed ender pearl): snaps the position-authoritative
        /// local player to the impact point and clears its fall accumulator so the jump isn't billed as a fall.</summary>
        private void UpdateTeleport()
        {
            if (!(_world.PendingTeleport is Vector3 target)) return;
            _world.PendingTeleport = null;

            _player.Position = target;
            _player.PrevPosition = target;
            _player.InterpolatedPosition = target;
            _player.Velocity = Vector3.Zero;
            PlayerController.ResetFall();
        }

        /// <summary>One fixed 20 tps simulation step: the player physics (only when <paramref name="stepPlayer"/>,
        /// i.e. focused and not paused), then the server tick, network pump, and the client's send-move.
        /// Called zero or more times per display frame by the accumulator in <see cref="Update"/>.</summary>
        private void Tick(bool stepPlayer)
        {
            PlayerController.Tick(_world, stepPlayer);
            UpdatePortalTint();

            var a = GC.GetAllocatedBytesForCurrentThread();
            _phaseTimer.Restart();
            _network?.TickWorlds();
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

        /// <summary>Ramps <see cref="_timeInPortal"/> toward 1 while the player stands in a portal block and back
        /// to 0 otherwise. Run on the fixed tick so the fill/fade rates are frame-rate-independent and stay in
        /// step with the server's soak; the client reads its own world replica, so no extra packet is needed.</summary>
        private void UpdatePortalTint()
        {
            var inPortal = PlayerInPortal();
            _timeInPortal = Math.Clamp(_timeInPortal + (inPortal ? 1f / 80f : -1f / 20f), 0f, 1f);
        }

        /// <summary>True if a portal block fills the player's column from feet to head — the same probe the
        /// server uses to trigger the transfer (<c>ServerNetwork.TryFindPortalCell</c>).</summary>
        private bool PlayerInPortal()
        {
            var portals = GameRegistry.Portals;
            if (portals == null) return false;

            var p = _player.Position;
            var bx = (int) MathF.Floor(p.X);
            var bz = (int) MathF.Floor(p.Z);
            var by = (int) MathF.Floor(p.Y + 0.2f);
            for (var dy = 0; dy <= 1; dy++)
                if (portals.IsPortalBlock(_world.GetBlock(bx, by + dy, bz))) return true;
            return false;
        }

        /// <summary>Pumps the world during the join handshake: applies the seed-derived spawn from
        /// LoginAccept, then hands control to the player once the server signals the spawn area is
        /// streamed (PlayerReady) and the client has actually decoded those chunks (so gravity has
        /// ground to land on). The same packet path drives this for singleplayer and multiplayer; the
        /// timeout is only an anti-hang safety against a dropped signal.</summary>
        private void UpdateLoading()
        {
            _network?.TickWorlds();
            _network?.Pump();
            _world.Update();

            if (_world.SpawnReceived && !_spawnApplied)
            {
                _player.Position = _world.SpawnPosition;
                _player.PrevPosition = _world.SpawnPosition;
                _player.InterpolatedPosition = _world.SpawnPosition;
                _player.Velocity = Vector3D<float>.Zero;
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
                if (_benchmark)
                {
                    if (Inspect.Enabled) Inspect.Begin(_player.Position);
                    else Benchmark.Begin(_player.Position);
                }
            }
        }

        // PlayerReady means the server has *sent* the spawn column; this confirms the client's apply
        // thread has actually decoded it into LoadedChunks, so the player has ground before physics start.
        private bool SpawnChunksApplied()
        {
            // Only the feet chunk is required: the chunk below can be all-air (never streamed), and the server's
            // ready signal already gates on it being loaded-or-empty, so requiring it here would re-introduce the
            // empty-below-chunk hang.
            var feetChunk = WorldBase.ChunkInWorld(_player.Position.ToVector3i());
            return _world.LoadedChunks.ContainsKey(feetChunk);
        }

        public override void Render()
        {
            if (_connectionFailed) return;

            if (_loading)
            {
                RenderLoading();
                return;
            }

            var aspect = (float)ClientResources.Width / ClientResources.Height;
            // Sprinting widens the FOV (eased in PlayerController), clamped so a high base FOV can't exceed 179°.
            var fov = MathF.Min(GraphicsSettings.Fov * PlayerController.FovScale, 179f);
            // Infinite-far reverse-Z: no far clip (the LOD horizon always reaches), and float depth keeps
            // near-uniform precision to infinity, so the distant ring never z-fights against near terrain.
            // Distance is bounded by the GPU cull compute, not a far plane. The near plane stays at 0.1 so it
            // only clips when the camera is right against a block.
            var projection = Projection.ReverseZPerspective(
                (fov * (MathF.PI / 180f)), aspect, 0.1f);
            WorldRenderer.RenderWorld(_world, projection);

            // Nether-portal screen tint, drawn over the world but under the HUD. Eased so it stays faint as the
            // soak begins and floods the screen as it completes (vanilla's t^4 boost shaped for a flat fill).
            if (_timeInPortal > 0f)
            {
                var alpha = _timeInPortal * _timeInPortal * 0.8f;
                GuiRenderer.DrawTexture(ClientResources.WhitePixel, new Vector4D<float>(0f, 0f, 1f, 1f),
                    new Vector4D<float>(0f, 0f, 1f, 1f), new Vector4D<float>(0.40f, 0.13f, 0.62f, alpha));
            }

            if (ClientResources.Input.CursorMode == CursorMode.Raw && !PlayerController.RenderSelf)
                CrosshairRenderer.Render();
            HotbarRenderer.Render(_world.Inventory);
            SurvivalHud.Render(_world);

            if (!Profiler.Recording && !RenderDebug.ShowDiagnostics && !RenderDebug.ShowControls) return;

            var y = 4;
            if (Profiler.Recording)
            {
                Font.DrawString("● REC", 4, y, 2, new Vector4D<float>(1f, 0.3f, 0.3f, 1f));
                y += Font.LineHeight(2) + 4;
            }

            if (RenderDebug.ShowDiagnostics) y = DrawDiagnostics(y);
            if (RenderDebug.ShowControls) DrawControls(y);
        }

        private static readonly Vector4D<float> OverlayText = new Vector4D<float>(1f, 1f, 1f, 1f);
        private static readonly Vector4D<float> OverlayHeader = new Vector4D<float>(0.55f, 0.8f, 1f, 1f);

        private void RenderLoading()
        {
            _loadingBackground ??= GlResources.ReadTexture("System/Textures/Gui/ResourceLoadingBackground.png");
            _loadingProgressBar ??= GlResources.ReadTexture("System/Textures/Gui/Progressbar.png");
            _loadingProgressBarFull ??= GlResources.ReadTexture("System/Textures/Gui/ProgressbarFull.png");

            GuiRenderer.DrawTexture(_loadingBackground,
                new Rectangle(0, 0, (int)ScaledResolution.GuiResolution.X, (int)ScaledResolution.GuiResolution.Y), null);

            // Ease the bar toward the target each frame so streaming bursts don't make it jump.
            _loadProgress += (LoadProgressTarget() - _loadProgress) * 0.1f;
            var pct = (int)(_loadProgress * 100f);

            var msg = $"Generating world... {pct}%";
            const int scale = 3;
            var x = (int)ScaledResolution.GuiResolution.X / 2 - Font.MeasureWidth(msg, scale) / 2;
            var y = (int)ScaledResolution.GuiResolution.Y / 2 - Font.LineHeight(scale) - 24;
            Font.DrawString(msg, x, y, scale, OverlayText);

            const int barX = 130;
            const int barTop = 300;
            const int barW = 700;
            const int barH = 30;
            GuiRenderer.DrawTexture(_loadingProgressBar, Rectangle.FromSize(barX, barTop, barW, barH), null);
            GuiRenderer.DrawTexture(_loadingProgressBarFull,
                Rectangle.FromSize(barX, barTop, (int)(barW * _loadProgress), barH), null);
        }

        /// <summary>Target loading progress (0..1). Staged by the join handshake — connecting, then the spawn
        /// region streaming in (smooth motion via the streamed-chunk count), then ready — and capped below 1
        /// until the player can actually drop in, so the bar never reads full while there is still a wait.</summary>
        private float LoadProgressTarget()
        {
            if (!_world.SpawnReceived) return 0.05f;
            if (_world.Ready && SpawnChunksApplied()) return 1f;

            // SpawnChunkStreamTarget is an estimate of the chunks streamed by the time the spawn area is ready;
            // it only paces the bar, the real gate is SpawnChunksApplied above.
            const int spawnChunkStreamTarget = 96;
            var frac = Math.Clamp(_world.LoadedChunks.Count / (float)spawnChunkStreamTarget, 0f, 1f);
            return Math.Clamp(0.1f + 0.85f * frac, 0.1f, 0.95f);
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
            ("Hotbar slot", "1 - 9 / scroll"),
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

            var srvLod = _integratedServer != null ? $" / srv {_integratedServer.LodStore.RegionCount}" : "";
            Font.DrawString($"lod drawn {RenderDebug.LodDrawn} / {_world.LodRegionCount}{srvLod}" +
                            $"   readyQ {_world.LodRenderReadyQueueDepth}", 4, y, scale, OverlayText); y += lh;

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
            ClientProfiling.World = null;
            Profiler.Network = null;
            Profiler.Server = null;

            // GPU teardown must run here, on the main/GPU thread. Stopping the server network and joining +
            // saving the world is GPU-free but can take seconds when many chunks are dirty (a burning furnace
            // floods light, dirtying every chunk it touches). Doing it here would block the render loop for the
            // whole save; on macOS the window then goes unresponsive and its drawable surface wedges, so the
            // screen freezes on the last frame even though the game has already moved to the menu. Run it on a
            // background thread instead; the next world open and app exit gate on it via WaitForPendingTeardown.
            _world?.Disconnect();

            var network = _network;
            if (network == null) return;

            _pendingTeardown = new Thread(() =>
            {
                network.Stop();
                network.UnloadWorlds();   // saves + stops every dimension world, not just the Overworld
            }) {Name = "World Teardown"};
            _pendingTeardown.Start();
        }

        /// <summary>Blocks until the previous world's async teardown (<see cref="Exit"/>) has finished saving.
        /// The teardown thread is foreground, so the process already won't exit mid-save; this also lets a
        /// freshly opened world wait so it never races the old save on disk.</summary>
        public static void WaitForPendingTeardown()
        {
            var pending = _pendingTeardown;
            if (pending != null && pending.IsAlive) pending.Join();
            _pendingTeardown = null;
        }

        /// <summary>True while the async world teardown (<see cref="Exit"/>) is still saving — drives the
        /// "Saving world..." screen, which keeps drawing until this clears and then reveals the title.</summary>
        public static bool IsTearingDown => _pendingTeardown != null && _pendingTeardown.IsAlive;
    }
}

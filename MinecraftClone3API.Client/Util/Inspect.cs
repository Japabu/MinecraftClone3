using System;
using System.Globalization;
using System.IO;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using Silk.NET.Maths;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// LOD inspection harness — the honest "what is the LOD actually doing" tool. Boots into the fixed-seed
    /// world (same as the benchmark), parks the camera at a set of fixed high-res poses framing the worst LOD
    /// cases (grazing distant terrain, looking down across the LOD rings), and at each pose captures the SAME
    /// view twice — once with LOD forced off (full detail = ground truth), once with LOD on — plus an amplified
    /// per-pixel <b>difference</b> image. The diff makes every artifact the LOD introduces (black faces, moved
    /// silhouettes from stair-stepping, colour/lighting shifts) light up, instead of being hand-waved away in a
    /// small flattering screenshot. Run with <c>--inspect</c>; it writes <c>inspect-&lt;pose&gt;-{full,lod,diff}.png</c>
    /// and exits. Separate from <see cref="Benchmark"/> (which measures FPS over a moving path).
    /// </summary>
    public static class Inspect
    {
        public static bool Enabled;
        public static bool Active { get; private set; }
        public static bool Finished { get; private set; }

        // Large window so artifacts are actually visible (720p hides them — that's how they got missed).
        public static int Width = 1920;
        public static int Height = 1080;
        public static int RenderDistanceChunks = 16;
        public static double FixedTimeOfDay = 220.0;
        public static Vector3D<float> OriginShift;   // diagnostic: anchor the poses this far from the world origin

        private struct Pose
        {
            public string Name;
            public Vector3D<float> Offset;  // from the spawn origin
            public float Yaw;       // radians
            public float Pitch;     // radians
        }

        // Poses framing the MID-DISTANCE terrain where LOD1/2 kick in (96–160 blocks) and the artifacts live —
        // before the distance fog (~184 blocks) hides them. Modelled on the benchmark's terrain-rich +X cruise
        // view (alt ~42, pitch ~-18°), with a steeper down-look that crosses the LOD0→1→2 rings and an off-axis
        // pan. (A low horizontal look mostly framed sky + fog, which is why full==LOD there.)
        private static readonly Pose[] Poses =
        {
            new Pose { Name = "terrain",   Offset = new Vector3D<float>(67, 42, 0),  Yaw = 0f,    Pitch = -0.32f },
            new Pose { Name = "downrings", Offset = new Vector3D<float>(50, 58, 0),  Yaw = 0f,    Pitch = -0.55f },
            new Pose { Name = "offaxis",   Offset = new Vector3D<float>(90, 44, 16), Yaw = 0.55f, Pitch = -0.34f },
            // Phase-2: look OUT to the distant LOD horizon from up high (terrain should recede continuously into
            // fog far past the 256-block render distance, no hard chunk edge); and a high down-look across the
            // detail→LOD seam to check for holes / double-images.
            new Pose { Name = "horizon",   Offset = new Vector3D<float>(0, 90, 0),   Yaw = 0f,    Pitch = -0.14f },
            new Pose { Name = "farseam",   Offset = new Vector3D<float>(0, 150, 0),  Yaw = 0.4f,  Pitch = -0.42f },
        };

        private enum State { Stream, FullSettle, CaptureFull, LodSettle, CaptureLod, Advance, Done }

        // Wall-clock based (the loop runs at hundreds of fps, so frame counts are fragile): a state is "ready"
        // once it has held its settle condition for SettleSeconds, with a per-state hard timeout.
        private const double SettleSeconds = 0.5;
        private const double MinStateSeconds = 0.1;
        private const double StateTimeoutSeconds = 30.0;

        private static Vector3D<float> _origin;
        private static int _poseIndex;
        private static State _state;
        private static int _lastLoaded = -1;
        private static int _lastLodRegions = -1;
        private static byte[] _fullPixels;
        private static readonly System.Diagnostics.Stopwatch _stateTimer = new System.Diagnostics.Stopwatch();
        private static readonly System.Diagnostics.Stopwatch _sinceUnsettled = new System.Diagnostics.Stopwatch();
        private static readonly System.Diagnostics.Stopwatch _fillTimer = new System.Diagnostics.Stopwatch();

        public static void Configure(string[] args)
        {
            foreach (var raw in args)
            {
                var arg = raw.Trim();
                if (arg.Equals("--inspect", StringComparison.OrdinalIgnoreCase)) { Enabled = true; continue; }
                if (!arg.StartsWith("--inspect-", StringComparison.OrdinalIgnoreCase)) continue;
                var eq = arg.IndexOf('=');
                if (eq < 0) continue;
                var key = arg.Substring(2, eq - 2).ToLowerInvariant();
                var val = arg.Substring(eq + 1);
                switch (key)
                {
                    case "inspect-width": int.TryParse(val, out Width); break;
                    case "inspect-height": int.TryParse(val, out Height); break;
                    case "inspect-rd": int.TryParse(val, out RenderDistanceChunks); break;
                    case "inspect-time": double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out FixedTimeOfDay); break;
                    case "inspect-offset":
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var off))
                            OriginShift = new Vector3D<float>(off, 0, off);
                        break;
                }
            }
        }

        public static void ApplySettings()
        {
            GraphicsSettings.SuppressSave = true;
            GraphicsSettings.RenderDistanceChunks = RenderDistanceChunks;
            GraphicsSettings.ShadowQuality = ShadowQuality.Off;
            WorldRenderer.FixedTimeOfDay = FixedTimeOfDay;
        }

        public static void Begin(Vector3D<float> origin)
        {
            if (Active) return;
            _origin = origin;
            _poseIndex = 0;
            _state = State.Stream;
            _stateTimer.Restart();
            _sinceUnsettled.Restart();
            _fillTimer.Restart();
            Active = true;
            Profiler.Start();   // capture the fill so the streaming pipeline can be profiled
            Logger.Info($"[inspect] started — {Poses.Length} poses at {Width}x{Height}, rd {RenderDistanceChunks}");
        }

        /// <summary>Parks the camera at the current pose every frame (stationary so the A/B captures align).</summary>
        public static void DriveCamera(EntityPlayer player)
        {
            if (!Active || _poseIndex >= Poses.Length) return;
            var pose = Poses[_poseIndex];
            var pos = _origin + OriginShift + pose.Offset;
            player.Position = pos;
            player.PrevPosition = pos;
            player.InterpolatedPosition = pos;
            player.Velocity = Vector3D<float>.Zero;
            player.Yaw = pose.Yaw;
            player.Pitch = pose.Pitch;
            player.Rotate(0, 0);
        }

        /// <summary>State machine + captures. Main-thread GPU (called from the render loop after the frame is
        /// drawn, before SwapBuffers). Drives the per-pose settle → full-detail capture → LOD capture → diff.</summary>
        public static void Tick(int fbWidth, int fbHeight)
        {
            if (!Active) return;
            var world = ClientProfiling.World;
            if (world == null) return;

            // The Stream state must wait for the world to stream in FULLY: the server finished loading (loaded
            // count stable + its staging queue drained) AND the client finished decoding/meshing/uploading.
            // Other states only re-mesh (no new loading), so they just need the client queues empty.
            var loaded = world.LoadedChunkCount;
            var lodRegions = world.LodRegionCount;
            var stageEmpty = (Profiler.Server?.StageQueueDepth ?? 0) == 0;
            // Stream also waits for the Phase-2 LOD horizon to finish filling (region count stable + decode
            // queue drained), since the LOD thread streams it in slowly after the detail chunks.
            var settleCond = _state == State.Stream
                ? QueuesEmpty(world) && stageEmpty && loaded == _lastLoaded && lodRegions == _lastLodRegions
                : QueuesEmpty(world);
            _lastLoaded = loaded;
            _lastLodRegions = lodRegions;
            if (!settleCond) _sinceUnsettled.Restart();

            var t = _stateTimer.Elapsed.TotalSeconds;
            var ready = (t >= MinStateSeconds && _sinceUnsettled.Elapsed.TotalSeconds >= SettleSeconds)
                        || t >= StateTimeoutSeconds;
            if (!ready) return;

            switch (_state)
            {
                case State.Stream:               // fully streamed at the pose (LOD as normal)
                    if (_poseIndex == 0)
                        Logger.Info($"[inspect] world streamed in {_fillTimer.Elapsed.TotalSeconds:0.0}s " +
                                    $"({loaded} chunks loaded)");
                    LogLodSteps(world);
                    Goto(State.FullSettle, () => { world.ForceLodOff = true; world.RemeshAll(); });
                    break;

                case State.FullSettle:           // everything re-meshed at full detail
                    Goto(State.CaptureFull);
                    break;

                case State.CaptureFull:
                    _fullPixels = Screenshot.ReadBackBuffer(fbWidth, fbHeight);
                    Goto(State.LodSettle, () => { world.ForceLodOff = false; world.RemeshAll(); });
                    break;

                case State.LodSettle:            // re-meshed back to the distance LOD
                    Goto(State.CaptureLod);
                    break;

                case State.CaptureLod:
                    WriteCaptures(fbWidth, fbHeight, Screenshot.ReadBackBuffer(fbWidth, fbHeight));
                    Goto(State.Advance);
                    break;

                case State.Advance:
                    _poseIndex++;
                    if (_poseIndex >= Poses.Length) Finish();
                    else Goto(State.Stream);
                    break;
            }
        }

        private static void WriteCaptures(int w, int h, byte[] lodPixels)
        {
            var name = Poses[_poseIndex].Name;
            try
            {
                var dir = GamePaths.UserDataDir;
                Screenshot.WritePng(Path.Combine(dir, $"inspect-{name}-full.png"), w, h, _fullPixels);
                Screenshot.WritePng(Path.Combine(dir, $"inspect-{name}-lod.png"), w, h, lodPixels);
                Screenshot.WriteDiff(Path.Combine(dir, $"inspect-{name}-diff.png"), w, h, _fullPixels, lodPixels);
                Logger.Info($"[inspect] pose '{name}' captured -> inspect-{name}-{{full,lod,diff}}.png");
            }
            catch (Exception e) { Logger.Warn("[inspect] capture write failed: " + e.Message); }
        }

        private static void Goto(State next, Action onEnter = null)
        {
            onEnter?.Invoke();
            _state = next;
            _stateTimer.Restart();
            _sinceUnsettled.Restart();
        }

        private static bool QueuesEmpty(WorldClient world) =>
            world.MeshQueueDepth == 0 && world.UploadQueueDepth == 0 &&
            world.RenderReadyQueueDepth == 0 && world.ApplyQueueDepth == 0 &&
            world.LodRenderReadyQueueDepth == 0;

        private static void LogLodSteps(WorldClient world)
        {
            var pose = Poses[_poseIndex];
            var p = _origin + OriginShift + pose.Offset;
            int s1 = 0, s2 = 0, s4 = 0, s8 = 0, other = 0;
            var minD = float.MaxValue;
            var maxD = 0f;
            var list = world.LodRenderList;
            for (var i = 0; i < list.Count; i++)
            {
                var rd = list[i];
                var dx = rd.Middle.X - p.X;
                var dz = rd.Middle.Z - p.Z;
                var d = MathF.Sqrt(dx * dx + dz * dz);
                if (d < minD) minD = d;
                if (d > maxD) maxD = d;
                switch (rd.DesiredMeshStep)
                {
                    case 1: s1++; break;
                    case 2: s2++; break;
                    case 4: s4++; break;
                    case 8: s8++; break;
                    default: other++; break;
                }
            }
            Logger.Info($"[inspect] LOD steps @pose '{pose.Name}' player=({p.X:0},{p.Z:0}) regions={list.Count} " +
                        $"step1={s1} step2={s2} step4={s4} step8={s8} other={other} " +
                        $"distXZ=[{(list.Count == 0 ? 0 : minD):0}..{maxD:0}] " +
                        $"rd={GraphicsSettings.RenderDistanceChunks} q={GraphicsSettings.LodHorizonQuality:0.0}");
        }

        private static void Finish()
        {
            _state = State.Done;
            Active = false;
            Finished = true;
            Logger.Info("[inspect] done");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Headless, deterministic "flythrough" benchmark — the GTA-style profiling session. When enabled (see
    /// <see cref="Configure"/>, driven by the client's <c>--benchmark</c> CLI flag) the game boots straight
    /// into a fresh fixed-seed world and an automated camera flies a scripted path that exercises the whole
    /// chunk pipeline: a streaming sweep into virgin terrain (gen + mesh fill), a 360° orbit (frustum / shadow
    /// churn), a return sweep over evicted ground (re-stream / regen + despawn), and an edit-stress pass
    /// (break/place → light BFS → delta → remesh). The day clock is pinned so every run sees identical sun /
    /// shadow conditions, so two runs are directly comparable.
    ///
    /// <para>It records the full <see cref="Profiler"/> CSV + <see cref="ChunkTracer"/> for the recorded window
    /// and, when the run ends, prints a percentile report (avg / P1-low / P0.1-low FPS, frame-time and GPU/CPU
    /// breakdowns, per-phase splits, GC) to stdout and writes it next to the CSVs, then closes the window so the
    /// process exits with a clean exit summary.</para>
    /// </summary>
    public static class Benchmark
    {
        /// <summary>Set by the CLI parse; the resource-loading state launches into the benchmark instead of the
        /// main menu when true.</summary>
        public static bool Enabled;

        /// <summary>True from <see cref="Begin"/> (world entered) until the run finishes — the world state
        /// drives the scripted camera instead of player input while this holds.</summary>
        public static bool Active { get; private set; }

        /// <summary>Set when the run completes; the game loop polls this to close the window and exit.</summary>
        public static bool Finished { get; private set; }

        // ── Config (deterministic defaults; overridable via Configure) ───────────────────────────────────
        // All quality settings are PINNED (not read from the user's file) so two runs are directly comparable
        // regardless of what the player last set; SuppressSave keeps the user's GraphicsSettings.json untouched.
        public static long Seed = 1337;
        public static double WarmupSeconds = 6.0;
        public static double DurationSeconds = 60.0;
        public static int RenderDistanceChunks = 8;        // representative mid render distance
        public static ShadowQuality Shadows = ShadowQuality.Medium;
        public static float Fov = 90f;
        public static float Brightness = 0.08f;
        public static float LodHorizonQuality = 1.0f;   // horizon detail-ring quality multiplier (GraphicsSettings)
        public static int LodHorizonChunks = 64;           // Phase-2 far-horizon chunks beyond render distance
        public static bool DoEdits = true;
        // Pins the day clock so sun/shadow conditions are identical every run (≈ mid-morning: sun well up,
        // long shadows, the shadow passes active — the heavy, representative case). See WorldRenderer.
        public static double FixedTimeOfDay = 220.0;
        public static float OriginOffset;   // diagnostic: shift spawn/path this far from the world origin (X=Z)

        // ── Scripted path tuning (blocks, blocks/s, radians) ─────────────────────────────────────────────
        private const float CruiseAltitude = 42f;     // height above the spawn surface for the sweeps
        private const float CruiseSpeed = 30f;        // forward speed of the streaming/return sweeps
        private const float OrbitRadius = 110f;
        private const float OrbitRevolutions = 1.6f;
        private const float EditAltitude = 6f;
        private const float EditSpeed = 7f;
        private const double EditsPerSecond = 18.0;

        private enum Phase { Warmup, Streaming, Orbit, Return, Edit, Done }

        private static readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
        private static Vector3 _origin;
        private static Phase _phase = Phase.Warmup;
        private static bool _recording;
        private static double _recordStartSeconds;

        // Anchors captured at each phase transition so the path is continuous (no teleport spikes).
        private static Vector3 _phaseAnchor;
        private static float _orbitStartAngle;
        private static int _editsDone;

        // GC / alloc bookmarks taken when recording starts.
        private static long _gc0Start, _gc1Start, _gc2Start, _allocStart;
        private static int _peakLoadedChunks;

        private struct Sample
        {
            public byte Phase;
            public float FrameMs;
            public float GpuMs;
            public float UpdateMs;
            public float RenderMs;
            public float ShadowMs;
            public float GeomMs;
            public float CompMs;
            public int DrawnChunks;
        }

        private static readonly List<Sample> _samples = new List<Sample>(64 * 1024);

        // Frames to snapshot to PNG (recorded-elapsed fraction → name), so a render change can be checked
        // visually, not just by FPS. Taken once each when the recorded clock passes the fraction.
        private static readonly (double Frac, string Name)[] _captureSchedule =
        {
            (0.16, "streaming"), (0.62, "return"), (0.90, "edit")
        };
        private static readonly bool[] _captureTaken = new bool[3];

        /// <summary>Applies CLI/env config. Call once before the window is created.</summary>
        public static void Configure(string[] args)
        {
            foreach (var raw in args)
            {
                var arg = raw.Trim();
                if (arg.Equals("--benchmark", StringComparison.OrdinalIgnoreCase)) { Enabled = true; continue; }
                if (!arg.StartsWith("--benchmark-", StringComparison.OrdinalIgnoreCase)) continue;

                var eq = arg.IndexOf('=');
                if (eq < 0) continue;
                var key = arg.Substring(2, eq - 2).ToLowerInvariant();
                var val = arg.Substring(eq + 1);

                switch (key)
                {
                    case "benchmark-seconds": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) DurationSeconds = d; break;
                    case "benchmark-warmup": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) WarmupSeconds = w; break;
                    case "benchmark-seed": if (long.TryParse(val, out var s)) Seed = s; break;
                    case "benchmark-rd": if (int.TryParse(val, out var rd)) RenderDistanceChunks = rd; break;
                    case "benchmark-lodquality": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var ld)) LodHorizonQuality = ld; break;
                    case "benchmark-lodhorizon": if (int.TryParse(val, out var lh)) LodHorizonChunks = lh; break;
                    case "benchmark-edits": DoEdits = !(val.Equals("off", StringComparison.OrdinalIgnoreCase) || val == "0"); break;
                    case "benchmark-time": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) FixedTimeOfDay = t; break;
                    case "benchmark-offset": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var of)) OriginOffset = of; break;
                    case "benchmark-shadows":
                        if (Enum.TryParse<ShadowQuality>(val, true, out var sq)) Shadows = sq;
                        break;
                }
            }

            if (Environment.GetEnvironmentVariable("MC3_BENCHMARK") == "1") Enabled = true;
        }

        /// <summary>Applies the deterministic settings overrides (vsync off is forced by the window; this sets
        /// the in-memory render distance / shadow quality / day clock without persisting to the user's file).</summary>
        public static void ApplySettings()
        {
            GraphicsSettings.SuppressSave = true;
            GraphicsSettings.RenderDistanceChunks = RenderDistanceChunks;
            GraphicsSettings.ShadowQuality = Shadows;
            GraphicsSettings.Fov = Fov;
            GraphicsSettings.Brightness = Brightness;
            GraphicsSettings.LodHorizonQuality = LodHorizonQuality;
            GraphicsSettings.LodHorizonChunks = LodHorizonChunks;
            WorldRenderer.FixedTimeOfDay = FixedTimeOfDay;
        }

        /// <summary>Wipes and recreates the dedicated benchmark world (a fixed dir + seed) so every run measures
        /// fresh terrain generation, not a disk reload of a previous run.</summary>
        public static WorldInfo CreateWorldInfo()
        {
            var dir = Path.Combine(GamePaths.WorldsDir, "__benchmark__");
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception e) { Logger.Warn("[benchmark] could not wipe world dir: " + e.Message); }

            var meta = new WorldMetadata { Name = "__benchmark__", Seed = Seed, LastPlayed = DateTime.Now };
            WorldMetadata.Save(dir, meta);
            return new WorldInfo { Directory = dir, Name = meta.Name, Seed = Seed, LastPlayed = meta.LastPlayed };
        }

        /// <summary>Called by the world state once the join handshake completes and the player is at the spawn.
        /// Anchors the path origin and starts the timeline; recording begins after the warmup window.</summary>
        public static void Begin(Vector3 origin)
        {
            if (Active) return;
            _origin = origin + new Vector3(OriginOffset, 0, OriginOffset);
            _phaseAnchor = _origin;
            _phase = Phase.Warmup;
            Active = true;
            _clock.Restart();
            Logger.Info($"[benchmark] started — seed {Seed}, rd {GraphicsSettings.RenderDistanceChunks}, " +
                        $"shadows {GraphicsSettings.ShadowQuality}, warmup {WarmupSeconds:0.#}s, record {DurationSeconds:0.#}s");
        }

        private static double Elapsed => _clock.Elapsed.TotalSeconds;

        /// <summary>Recorded-window elapsed (negative during warmup).</summary>
        private static double RecordedElapsed => Elapsed - WarmupSeconds;

        /// <summary>Drives the automated camera for the current point on the scripted path and, in the edit
        /// phase, issues block edits. Called from the world state's Update in place of player input. The path is
        /// a pure function of elapsed time, so it is frame-rate independent and reproducible.</summary>
        public static void DriveCamera(EntityPlayer player, WorldBase world)
        {
            if (!Active) return;

            var t = Elapsed;
            var rt = RecordedElapsed;
            var newPhase = PhaseAt(rt);
            if (newPhase != _phase) EnterPhase(newPhase, player);

            Vector3 pos;
            float yaw, pitch;

            switch (_phase)
            {
                case Phase.Warmup:
                {
                    // Rise to cruise altitude and slowly spin to load the spawn neighbourhood before recording.
                    var rise = (float) Math.Min(1.0, t / Math.Max(0.5, WarmupSeconds * 0.6));
                    pos = _origin + new Vector3(0, CruiseAltitude * rise, 0);
                    yaw = (float) (t * 0.5);
                    pitch = -0.25f;
                    break;
                }
                case Phase.Streaming:
                {
                    // Straight heading into virgin terrain; gentle yaw/altitude oscillation to pan.
                    var local = (float) (rt - PhaseStart(Phase.Streaming));
                    pos = _phaseAnchor + new Vector3(CruiseSpeed * local, 4f * (float) Math.Sin(local * 0.6), 0);
                    yaw = 0.45f * (float) Math.Sin(local * 0.4);
                    pitch = -0.32f;
                    if ((int) local != _lastLodLogSecond) { _lastLodLogSecond = (int) local; SampleLodHealth(pos); }
                    break;
                }
                case Phase.Orbit:
                {
                    var local = (float) (rt - PhaseStart(Phase.Orbit));
                    var dur = PhaseDuration(Phase.Orbit);
                    var ang = _orbitStartAngle + (float) (local / dur) * OrbitRevolutions * MathHelper.TwoPi;
                    var center = _phaseAnchor;
                    pos = center + new Vector3(MathF.Cos(ang) * OrbitRadius,
                        6f * (float) Math.Sin(local * 0.5), MathF.Sin(ang) * OrbitRadius);
                    // Face along the tangent (direction of travel) so the camera always moves into fresh view.
                    yaw = ang + MathHelper.PiOver2;
                    pitch = -0.28f;
                    break;
                }
                case Phase.Return:
                {
                    // Fly back toward the spawn over already-visited (now evicted) ground → re-stream / regen.
                    var local = (float) (rt - PhaseStart(Phase.Return));
                    var dur = PhaseDuration(Phase.Return);
                    var target = _origin + new Vector3(0, CruiseAltitude, 0);
                    var frac = (float) Math.Min(1.0, local / dur);
                    pos = Vector3.Lerp(_phaseAnchor, target, Smooth(frac))
                          + new Vector3(0, 4f * (float) Math.Sin(local * 0.6), 0);
                    var dir = target - _phaseAnchor;
                    yaw = MathF.Atan2(dir.X, dir.Z);
                    pitch = -0.3f;
                    break;
                }
                case Phase.Edit:
                {
                    // Skim low over terrain near spawn, looking down, carving/placing blocks as we go.
                    var local = (float) (rt - PhaseStart(Phase.Edit));
                    pos = _phaseAnchor + new Vector3(EditSpeed * local,
                        EditAltitude - CruiseAltitude + 3f * (float) Math.Sin(local), EditSpeed * 0.4f * local);
                    yaw = 0.3f * (float) Math.Sin(local * 0.5);
                    pitch = -0.85f;
                    if (DoEdits) RunEdits(player, world, rt);
                    break;
                }
                default:
                    pos = player.Position;
                    yaw = player.Yaw;
                    pitch = player.Pitch;
                    break;
            }

            player.Position = pos;
            player.PrevPosition = pos;
            player.InterpolatedPosition = pos;
            player.Velocity = Vector3.Zero;
            player.Yaw = yaw;
            player.Pitch = pitch;
            player.Rotate(0, 0); // recompute Forward/Right from Yaw/Pitch
        }

        private static int _lastLodLogSecond = -1;
        private static float _peakLodUnmeshedFrac;   // worst fraction of visible LOD regions left unmeshed while moving

        /// <summary>Samples LOD horizon health while cruising: the fraction of regions inside the LOD draw distance
        /// that are NOT yet meshed/uploaded. A sustained high value means the LOD mesh pipeline is starving behind
        /// continuous movement (the "walk far → horizon goes coarse/empty" regression) — surfaced in the report.</summary>
        private static void SampleLodHealth(Vector3 p)
        {
            var world = ClientProfiling.World;
            if (world == null) return;
            var list = world.LodRenderList;
            var drawSq = world.LodRenderDistance * world.LodRenderDistance;
            int vis = 0, unmeshed = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var rd = list[i];
                var dx = rd.Middle.X - p.X;
                var dz = rd.Middle.Z - p.Z;
                if (dx * dx + dz * dz > drawSq) continue;   // only regions the player can actually see
                vis++;
                if (!rd.Uploaded) unmeshed++;
            }
            if (vis > 0)
            {
                var frac = (float) unmeshed / vis;
                if (frac > _peakLodUnmeshedFrac) _peakLodUnmeshedFrac = frac;
            }
        }

        private static void RunEdits(EntityPlayer player, WorldBase world, double rt)
        {
            var target = (int) ((rt - PhaseStart(Phase.Edit)) * EditsPerSecond);
            if (target <= _editsDone) return;
            _editsDone = target;

            var hit = world.BlockRaytrace(player.RenderPosition + player.EyeOffset, player.Forward, 12);
            if (hit == null) return;

            // Alternate carve / place so both the break and the place light/remesh paths are exercised.
            if ((_editsDone & 1) == 0)
                world.SetBlock(hit.BlockPos, BlockRegistry.BlockAir);
            else
                world.PlaceBlock(player, hit.BlockPos + hit.Face.GetNormali(), GameRegistry.GetBlock("Vanilla:Stone"), 0);
        }

        /// <summary>Per-frame sample + timeline advance. Called from the render loop right after
        /// <see cref="Profiler.Record"/> so it sees the same per-frame timings.</summary>
        public static void Tick(double frameSeconds, double updateMs, double renderMs, double gpuMs)
        {
            if (!Active) return;

            var rt = RecordedElapsed;

            if (!_recording && rt >= 0)
            {
                _recording = true;
                _recordStartSeconds = Elapsed;
                _gc0Start = GC.CollectionCount(0);
                _gc1Start = GC.CollectionCount(1);
                _gc2Start = GC.CollectionCount(2);
                _allocStart = GC.GetTotalAllocatedBytes();
                Profiler.Start();
                Logger.Info("[benchmark] recording…");
            }

            if (_recording && rt <= DurationSeconds)
            {
                var loaded = ClientProfiling.World?.LoadedChunkCount ?? 0;
                if (loaded > _peakLoadedChunks) _peakLoadedChunks = loaded;

                _samples.Add(new Sample
                {
                    Phase = (byte) PhaseAt(rt),
                    FrameMs = (float) (frameSeconds * 1000.0),
                    GpuMs = (float) gpuMs,
                    UpdateMs = (float) updateMs,
                    RenderMs = (float) renderMs,
                    ShadowMs = (float) GpuTimers.Ms(GpuTimers.Pass.Shadow),
                    GeomMs = (float) GpuTimers.Ms(GpuTimers.Pass.Geometry),
                    CompMs = (float) GpuTimers.Ms(GpuTimers.Pass.Composition),
                    DrawnChunks = RenderDebug.DrawnChunks
                });
            }

            // Authoritative end-of-run: fires exactly once (DriveCamera may already have flipped _phase to
            // Done for the camera path, so guard on Finished, not _phase).
            if (rt >= DurationSeconds && !Finished) Finish();
        }

        /// <summary>Main-thread GL: snapshots the back buffer to a PNG when the recorded clock passes a
        /// scheduled point. Called from the render loop after the frame is drawn, before SwapBuffers.</summary>
        public static void CaptureFrame(int width, int height)
        {
            if (!Active || !_recording) return;
            var rt = RecordedElapsed;
            for (var i = 0; i < _captureSchedule.Length; i++)
            {
                if (_captureTaken[i] || rt < _captureSchedule[i].Frac * DurationSeconds) continue;
                _captureTaken[i] = true;
                try
                {
                    var path = Path.Combine(GamePaths.UserDataDir, $"benchmark-{_captureSchedule[i].Name}.png");
                    Screenshot.CaptureBackBuffer(path, width, height);
                    Logger.Info($"[benchmark] captured frame -> {path}");
                }
                catch (Exception e) { Logger.Warn("[benchmark] capture failed: " + e.Message); }
            }
        }

        private static void Finish()
        {
            _phase = Phase.Done;
            var report = BuildReport();
            Profiler.Stop();

            try
            {
                var path = Path.Combine(GamePaths.UserDataDir, "benchmark-report.txt");
                File.WriteAllText(path, report);
                Logger.Info($"[benchmark] report -> {path}");
            }
            catch (Exception e) { Logger.Warn("[benchmark] could not write report: " + e.Message); }

            Console.WriteLine();
            Console.WriteLine(report);
            Console.Out.Flush();

            Active = false;
            Finished = true;
        }

        // ── Phase timeline (fractions of the recorded duration) ──────────────────────────────────────────
        private const double FStreaming = 0.32, FOrbit = 0.24, FReturn = 0.24, FEdit = 0.20;

        private static double PhaseStart(Phase p)
        {
            switch (p)
            {
                case Phase.Streaming: return 0;
                case Phase.Orbit: return DurationSeconds * FStreaming;
                case Phase.Return: return DurationSeconds * (FStreaming + FOrbit);
                case Phase.Edit: return DurationSeconds * (FStreaming + FOrbit + FReturn);
                default: return 0;
            }
        }

        private static double PhaseDuration(Phase p)
        {
            switch (p)
            {
                case Phase.Streaming: return DurationSeconds * FStreaming;
                case Phase.Orbit: return DurationSeconds * FOrbit;
                case Phase.Return: return DurationSeconds * FReturn;
                case Phase.Edit: return DurationSeconds * FEdit;
                default: return 0;
            }
        }

        private static Phase PhaseAt(double rt)
        {
            if (rt < 0) return Phase.Warmup;
            if (rt >= DurationSeconds) return Phase.Done;
            if (rt < PhaseStart(Phase.Orbit)) return Phase.Streaming;
            if (rt < PhaseStart(Phase.Return)) return Phase.Orbit;
            if (rt < PhaseStart(Phase.Edit)) return Phase.Return;
            return Phase.Edit;
        }

        private static void EnterPhase(Phase next, EntityPlayer player)
        {
            _phase = next;
            _phaseAnchor = player.Position;
            if (next == Phase.Orbit)
            {
                // Treat the entry position as a point on the circle; centre is offset toward -X so the orbit
                // sweeps over fresh ground rather than re-tracing the streaming line.
                _orbitStartAngle = 0f;
                _phaseAnchor = player.Position - new Vector3(OrbitRadius, 0, 0);
            }
            Logger.Info($"[benchmark] phase: {next}  (t={RecordedElapsed:0.0}s)");
        }

        private static float Smooth(float x) => x * x * (3f - 2f * x);

        // ── Report ───────────────────────────────────────────────────────────────────────────────────────
        private static string BuildReport()
        {
            var sb = new StringBuilder(2048);
            var dGc0 = GC.CollectionCount(0) - _gc0Start;
            var dGc1 = GC.CollectionCount(1) - _gc1Start;
            var dGc2 = GC.CollectionCount(2) - _gc2Start;
            var allocMB = (GC.GetTotalAllocatedBytes() - _allocStart) / (1024.0 * 1024.0);
            var wallSeconds = Elapsed - _recordStartSeconds;

            sb.AppendLine("================ BENCHMARK REPORT ================");
            sb.AppendLine($"seed={Seed}  renderDist={GraphicsSettings.RenderDistanceChunks}ch  " +
                          $"shadows={GraphicsSettings.ShadowQuality}  fixedTimeOfDay={FixedTimeOfDay:0}");
            sb.AppendLine($"recorded {wallSeconds:0.0}s  frames={_samples.Count}  peakLoadedChunks={_peakLoadedChunks}");
            sb.AppendLine();

            AppendSection(sb, "OVERALL", null);
            AppendSection(sb, "  streaming", Phase.Streaming);
            AppendSection(sb, "  orbit", Phase.Orbit);
            AppendSection(sb, "  return", Phase.Return);
            if (DoEdits) AppendSection(sb, "  edit", Phase.Edit);

            sb.AppendLine();
            sb.AppendLine($"LOD horizon:  peak visible-unmeshed {_peakLodUnmeshedFrac * 100:0}%  " +
                          "(high => LOD mesh pipeline starving behind movement)");
            sb.AppendLine($"GC over run:  gen0={dGc0}  gen1={dGc1}  gen2={dGc2}  totalAlloc={allocMB:0.0} MB" +
                          (_samples.Count > 0 ? $"  ({allocMB / _samples.Count * 1024:0.0} KB/frame)" : ""));
            sb.AppendLine("=================================================");
            return sb.ToString();
        }

        private static void AppendSection(StringBuilder sb, string label, Phase? phase)
        {
            var frame = Collect(phase, s => s.FrameMs);
            if (frame.Length == 0) { sb.AppendLine($"{label,-12}  (no samples)"); return; }

            Array.Sort(frame);
            var avgFrame = Mean(frame);
            var avgFps = avgFrame > 0 ? 1000.0 / avgFrame : 0;
            // "x% low" = average FPS of the worst x% of frames (longest frame times).
            var low1 = LowFps(frame, 0.01);
            var low01 = LowFps(frame, 0.001);
            var p99 = Percentile(frame, 0.99);
            var p95 = Percentile(frame, 0.95);

            sb.AppendLine($"{label,-12}  avgFPS {avgFps,6:0.0}   1%low {low1,6:0.0}   0.1%low {low01,6:0.0}   " +
                          $"| frameMs avg {avgFrame,5:0.00} p95 {p95,5:0.00} p99 {p99,6:0.00} max {frame[frame.Length - 1],6:0.00}");

            var gpu = Collect(phase, s => s.GpuMs);
            var upd = Collect(phase, s => s.UpdateMs);
            var rnd = Collect(phase, s => s.RenderMs);

            // Uncapped (present-independent) throughput: the engine produces a frame in max(gpu work, cpu work)
            // ms — the bottleneck of the pipelined CPU/GPU. The observed avgFPS above is throttled by the
            // display's present cap (~120 Hz here) whenever frame work < the refresh interval, so THIS is the
            // metric that actually reflects how fast the engine is and is comparable across machines/displays.
            var uncapped = Collect(phase, s => MathF.Max(s.GpuMs, s.UpdateMs + s.RenderMs));
            Array.Sort(uncapped);
            var avgUncappedMs = Mean(uncapped);
            var uncappedFps = avgUncappedMs > 0 ? 1000.0 / avgUncappedMs : 0;
            var uncappedLow1 = LowFps(uncapped, 0.01);
            sb.AppendLine($"{"",12}  UNCAPPED avgFPS {uncappedFps,6:0.0}  1%low {uncappedLow1,6:0.0}  " +
                          $"(frame work {avgUncappedMs,5:0.00} ms = max of gpu {Mean(gpu),4:0.00} / cpu {Mean(upd) + Mean(rnd),4:0.00})");
            var shadow = Collect(phase, s => s.ShadowMs);
            var geom = Collect(phase, s => s.GeomMs);
            var comp = Collect(phase, s => s.CompMs);
            var drawn = Collect(phase, s => s.DrawnChunks);
            sb.AppendLine($"{"",12}  gpu {Mean(gpu),5:0.00}ms (shadow {Mean(shadow),4:0.00} geom {Mean(geom),4:0.00} " +
                          $"comp {Mean(comp),4:0.00})  cpuUpd {Mean(upd),5:0.00}ms  cpuRnd {Mean(rnd),5:0.00}ms  drawnChunks {Mean(drawn),5:0.0}");
        }

        private static float[] Collect(Phase? phase, Func<Sample, float> sel)
        {
            var list = new List<float>(_samples.Count);
            foreach (var s in _samples)
                if (phase == null || s.Phase == (byte) phase.Value)
                    list.Add(sel(s));
            return list.ToArray();
        }

        private static float[] Collect(Phase? phase, Func<Sample, int> sel)
        {
            var list = new List<float>(_samples.Count);
            foreach (var s in _samples)
                if (phase == null || s.Phase == (byte) phase.Value)
                    list.Add(sel(s));
            return list.ToArray();
        }

        private static double Mean(float[] v)
        {
            if (v.Length == 0) return 0;
            double sum = 0;
            foreach (var x in v) sum += x;
            return sum / v.Length;
        }

        /// <summary>Percentile of an ascending-sorted array (0..1).</summary>
        private static double Percentile(float[] sorted, double q)
        {
            if (sorted.Length == 0) return 0;
            var idx = (int) Math.Round(q * (sorted.Length - 1));
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }

        /// <summary>Average FPS of the worst <paramref name="frac"/> of frames (largest frame times), the
        /// standard "1% low" metric. <paramref name="sortedFrameMs"/> must be ascending.</summary>
        private static double LowFps(float[] sortedFrameMs, double frac)
        {
            if (sortedFrameMs.Length == 0) return 0;
            var count = Math.Max(1, (int) (sortedFrameMs.Length * frac));
            double sum = 0;
            for (var i = sortedFrameMs.Length - count; i < sortedFrameMs.Length; i++) sum += sortedFrameMs[i];
            var avgMs = sum / count;
            return avgMs > 0 ? 1000.0 / avgMs : 0;
        }
    }
}

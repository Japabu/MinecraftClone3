# Debug & profiling tools

In-world keys (handled in `PlayerController.OnKeyDown`). The toggles + per-frame stats live in
`Client/Graphics/RenderDebug.cs` (a single static): `PlayerController` flips the toggles, `WorldRenderer`
reads them and writes the stats, `StateWorld.Render` draws the overlays, `Program.OnRender` writes frame
timings. F4 chunk borders are the one exception (kept on `ChunkBorderRenderer.Enabled`).

- **F1** — toggle the controls/help overlay: a fixed keybind list drawn top-left. `RenderDebug.ShowControls`.
- **F3** — toggle the on-screen diagnostics overlay: FPS + smoothed frame ms, `gpu`/`cpu upd` ms, **chunks
  drawn / total** (frustum-cull readout), shadows on/off, loaded/mesh/upload queue depths, a
  **pipeline-backlog line** (`apply`/`ready`/`dispose` client queue depths, plus `stage` = the server staging
  queue in SP), and player pos + chunk. `RenderDebug.ShowDiagnostics`, drawn from `RenderDebug` fields plus
  the `WorldClient`/`WorldServer` lock-free depth mirrors. The cheap always-available live HUD; the CSV
  profilers (F10) are the heavy tools. (Per-frame *sum* timers are kept off F3 — a single-frame sum flickers
  meaninglessly at 120 Hz; they live in the CSV.)
- **F2** — toggle a fixed time-of-day (pins the sun position for reproducible lighting).
  `WorldRenderer.FixedTimeOfDay`.
- **F4** — toggle chunk-border wireframes (current chunk red, neighbours yellow).
  `Client/Graphics/ChunkBorderRenderer.cs`, drawn in the geometry pass (depth-tested).
- **F5** — cycle the camera perspective: first-person → third-person behind → third-person facing the
  player. `PlayerController.Perspective` (`CameraPerspective`). (F6 unused.)
- **F7** — toggle the raw shadow-factor view: composition outputs the per-pixel shadow term as greyscale
  (white = lit, black = shadowed), to spot acne/peter-panning. `RenderDebug.ShadowFactor` → `uDebugShadow`.
- **F10** — toggle the frame profiler. Writes a CSV per render frame to
  `~/.local/share/MinecraftClone3/profiling.csv`. An on-screen `● REC` shows while recording; toggling off
  (or leaving the world) flushes and closes the file. Code: `Util/Profiler.cs`, fed from
  `Program.OnRender` + `StateWorld.Update` (phase times) and the `WorldServer`/`WorldClient` pipeline
  threads. Columns:
  - `t, frameMs, fps, updateMs, renderMs, swapMs, gapMs, gpuMs, shadowMs/geomMs/compMs (per-pass GPU),
    updCalls, gen0/1/2, dGen0/1/2 (per-frame GC events), heapMB, allocMB (per frame, all threads)`
  - `srvMB/netMB/cliMB/rndMB` — per-frame main-thread allocation split (server / networking / client world /
    render); `loadMB/lightMB/unloadMB/meshMB/applyMB` — per-frame background-thread allocation split.
  - `chunks, renderData, pendingMesh, entities, pcx/pcy/pcz (player chunk), borderCross`
  - `srvMs/netMs/cliMs` — the split of `updateMs` into the three `StateWorld.Update` calls (server sim /
    `Pump` / client world `Update`), so a spike is attributable to one. Sub-splits:
    `streamMs/flushMs/chStreamed/chDrained/chPkts` inside `netMs`;
    `pktMs/drainMs/upMs/evictMs` + `upChunks/upIndices/upQ` inside `cliMs`.
  - `diskMs/genMs/applyMs/meshMs/drainAddMs` — per-frame wall-clock of the heavy pipeline stages (the
    background ones are Interlocked tick sums attributed to the frame interval, so they overlap real time, not
    add to `updateMs`); `chFromDisk/chGenerated/chApplied/chMeshed/chDrainedAdd` — throughput per stage;
    `srvStageQ/applyQ/renderReadyQ/disposeQ` — pipeline queue depths, so a balloon localizes *which* stage is
    the wall.

  > ⚠️ **GPU-timer columns read 0.** `gpuMs`/`shadowMs`/`geomMs`/`compMs` are always 0 because `GpuTimers`
  > (`Client/Graphics/GpuTimers.cs`) is a safe no-op pending a `QuerySet` RHI wrapper for WebGPU timestamp
  > queries; `gapMs` is also 0 (no between-frame gap is carried on the Silk frame loop). The `chunks drawn` /
  > `lod drawn` **numerators** also read 0: under GPU-driven culling the cull compute owns the post-cull count
  > with no CPU readback, so the CPU never builds a visible set. The live columns are the CPU ones
  > (`frameMs`/`updateMs`/`renderMs`/`swapMs`) and the cull denominators.

  Reading the timers: `frameMs` is the real frame interval (catches drops); `updateMs`/`renderMs` are CPU
  work. `swapMs` = surface present (`Renderer.EndFrame` — the tonemap pass + GUI flush + `_frame.Present()`),
  the stall a CPU sampler can't see; a large `swapMs` ⇒ present back-pressure. When `updateMs` is large, the
  srv/net/cli splits localize it. `updCalls` ≫ 1 means updates are running behind and being batched. **The
  profiler reads only lock-free mirrors** (`volatile`/`Interlocked` depth fields), never
  `ConcurrentDictionary`/`ConcurrentQueue.Count` or a `_meshLock` take, so recording doesn't contend with the
  apply/mesh threads and inflate the very stutter it measures.

  **F10 also drives a second CSV split by *grain*** (a per-frame time-series vs a per-chunk latency log).
  `chunk-trace.csv` (`Util/ChunkTracer.cs`, same F10 toggle, same `t` clock so they correlate offline) is
  **one row per chunk**, emitted on upload finish, keyed by chunk position. Its schema is a **work-vs-wait
  decomposition** of the chunk's whole life across the 4 pipeline threads — columns `t, posX/Y/Z, source
  (disk/gen/edit/stream), mp`, then the tiling spans `genMs`(work) `stageWaitMs` `drainWaitMs` `streamWaitMs`
  `netWaitMs` `applyWaitMs` `applyMs`(work) `meshWaitMs` `meshMs`(work) `uploadWaitMs`, and `totalMs`.
  Adjacent stamps tile the timeline, so in SP the spans sum to `totalMs` (an instrumentation self-check); on a
  **multiplayer client** (`mp=1`) the server stages are blank and `totalMs` starts at `applyWaitMs`. A
  **block-edit** (or a re-stream after eviction) emits a separate `source=edit` row covering only the
  mesh→upload tail. Memory is bounded by a `MaxLive` cap + `Abandon` at
  the drop sites + a TTL sweep; every stamp early-outs on `!Profiler.Recording`, so a non-recording run pays
  nothing.

External (.NET global tools, no rebuild — attach to the running PID):

```
dotnet tool install -g dotnet-counters dotnet-trace dotnet-gcdump   # once
dotnet-counters monitor -p <pid> System.Runtime                     # live CPU/GC/heap/alloc-rate
dotnet-trace collect   -p <pid> --profile gc-verbose                # GC events -> .nettrace (PerfView/VS)
dotnet-trace collect   -p <pid>                                     # CPU sampling -> .nettrace
dotnet-gcdump collect  -p <pid>                                     # heap snapshot -> .gcdump
```

`pidof MinecraftClone3` (or `dotnet-counters ps`) to find the PID. Rider/Visual Studio have built-in CPU +
allocation + timeline profilers if preferred.

**GPU frame capture.** For per-draw GPU timing, overdraw, and shader/buffer inspection of the deferred
passes, capture a frame with a WebGPU-aware debugger (the platform native tool — Xcode's Metal frame debugger
on macOS, RenderDoc on Vulkan/D3D12). `GraphicsDebug` (`Client/Graphics/GraphicsDebug.cs`) wraps the frame encoder's
`PushDebugGroup`/`PopDebugGroup` so a capture can nest the passes as **Shadow / Geometry →
Opaque/Transparent/Overlays / ShadowResolve / Composition** and show per-group GPU time and tree structure —
though the per-pass `GraphicsDebug.PushGroup` calls aren't wired into the renderer yet. Object labels (the G-buffer/shadow targets, pipelines, buffers) are set
at resource creation in the RHI wrappers, so `GraphicsDebug.Label` is a no-op. Groups are issued only when
`Enabled` (`RENDERDOC_CAPOPTS` set, or `MC3_GL_DEBUG=1` / `MC3_FORCE_X11=1`), so normal runs pay nothing. **A
depth-only pass being the GPU bottleneck means geometry/draw-call-bound, not fill/shader-bound** — the shadow
pass redraws all in-range opaque chunks from the sun's POV, so the fix is reducing geometry submitted (shorter
`ShadowDistance`, smaller `ShadowMapSize`), not shader work.

**Automated flythrough benchmark (`--benchmark`).** The client boots straight into a fresh fixed-seed world
and an automated camera flies a deterministic scripted path while `Profiler` + `ChunkTracer` record, then
prints a percentile report and exits — the reliable, repeatable way to measure a render/pipeline change.
Code: `Util/Benchmark.cs`, wired from `Program.Main` (CLI parse + vsync-off + settings pin), `StateWorld`
(drives the camera via `Benchmark.DriveCamera` instead of player input), and `Program.OnRender`
(`Benchmark.Tick`/`Benchmark.CaptureFrame` per frame; the window closes when `Benchmark.Finished`).
**Benchmark Release, not Debug — Debug understates FPS hugely.**

```
bin/Release/net10.0/MinecraftClone3 --benchmark   # boots the flythrough, prints report, exits
  --benchmark-seconds=60   --benchmark-warmup=6    --benchmark-seed=1337
  --benchmark-rd=8         --benchmark-shadows=Medium   # Off|Low|Medium|High
  --benchmark-edits=off    --benchmark-time=220         # pinned day-clock seconds (sun pos)
  --benchmark-offset=N     # anchor the run N blocks from the world origin (far-from-origin behaviour)
```

The report (also written to `benchmark-report.txt`) gives overall + per-phase avg / 1%-low / 0.1%-low FPS,
the GPU per-pass split (shadow/geom/comp), CPU update/render ms, drawn chunks, GC/alloc, and **peak visible-
unmeshed %** (the LOD horizon's health — how much of the on-screen frustum lacked a mesh at the worst frame).

**LOD A/B-diff inspector (`--inspect`).** The honest "what does my render change actually do" tool, for
catching real LOD artifacts (black faces, stair-stepping) that low-res screenshots hide. `Util/Inspect.cs`
boots the fixed-seed world at a large window (default 1920×1080, `--inspect-width/-height`), **waits for the
world to fully stream in** (loaded count stable + server staging drained + client mesh/upload queues empty),
then at each fixed pose captures the *same* view twice — LOD forced off (`WorldClient.ForceLodOff` + `RemeshAll`,
the ground truth) and LOD on — writing `inspect-<pose>-{full,lod,diff}.png` to `UserDataDir`. The **diff** is
an amplified per-pixel `|full−lod|` (`Screenshot.WriteDiff`): a near-black image where every pixel the LOD
changed lights up, so a regression can't hide. `Read` the PNGs to review; pick poses framing **mid-distance
terrain** (where LOD1/2 kick in, before the fog) — a low horizontal look just frames sky+fog (full==LOD → black
diff, a non-result). `--inspect-offset=N` anchors the run N blocks from the world origin.

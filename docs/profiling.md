# Debug & profiling tools

In-world keys (handled in `PlayerController.Update`). The toggles + per-frame stats live in
`Client/Graphics/RenderDebug.cs` (a single static): `PlayerController` flips the toggles, `WorldRenderer`
reads them and writes the stats, `StateWorld.Render` draws the overlays, `GameClient` writes frame timings.
F4 chunk borders are the one exception (kept on `ChunkBorderRenderer.Enabled`).

- **F1** — toggle the controls/help overlay: a fixed keybind list drawn top-left. `RenderDebug.ShowControls`.
- **F3** — toggle the on-screen diagnostics overlay: FPS + smoothed frame ms, `gpu`/`cpu upd` ms, **chunks
  drawn / total** (frustum-cull readout), shadows on/off, loaded/mesh/upload queue depths, a
  **pipeline-backlog line** (`apply`/`ready`/`dispose` client queue depths, plus `stage` = the server staging
  queue in SP), and player pos + chunk. `RenderDebug.ShowDiagnostics`, drawn from `RenderDebug` fields plus
  the `WorldClient`/`WorldServer` lock-free depth mirrors. The cheap always-available live HUD; the CSV
  profilers (F10) are the heavy tools. (Per-frame *sum* timers are kept off F3 — a single-frame sum flickers
  meaninglessly at 120 Hz; they live in the CSV.)
- **F4** — toggle chunk-border wireframes (current chunk red, neighbours yellow).
  `Client/Graphics/ChunkBorderRenderer.cs`, drawn in the geometry pass (depth-tested). (F5/F6 unused.)
- **F7** — toggle the raw shadow-factor view: composition outputs the per-pixel shadow term as greyscale
  (white = lit, black = shadowed), to spot acne/peter-panning. `RenderDebug.ShadowFactor` → `uDebugShadow`.
- **F10** — toggle the frame profiler. Writes a CSV per render frame to
  `~/.local/share/MinecraftClone3/profiling.csv`. An on-screen `● REC` shows while recording; toggling off
  (or leaving the world) flushes and closes the file. Code: `Util/Profiler.cs`, fed from
  `GameClient.OnRenderFrame` + `StateWorld.Update` (phase times) and the `WorldServer`/`WorldClient` pipeline
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

  Reading the timers: `frameMs` is the real frame interval (catches drops); `updateMs`/`renderMs` are CPU
  work. The four stalls a CPU sampler can't see: `swapMs` = the `SwapBuffers` call; `gapMs` = OpenTK's
  between-frame `NewInputFrame`+`ProcessWindowEvents` (where an **async/vsync present surfaces on
  Linux/GLX**); `gpuMs` = actual GPU render time (`GL_TIME_ELAPSED`). `gpuMs` large ⇒ GPU-bound; `gpuMs`
  small but `gapMs` large ⇒ present/event overhead. `shadowMs`/`geomMs`/`compMs` split `gpuMs` into the
  three deferred passes via `GL_TIMESTAMP` markers (`Client/Graphics/GpuTimers.cs`, populated only while
  recording): large `shadowMs` ⇒ the depth pass is geometry/draw-call-bound, large `compMs` ⇒ composition is
  fill/shader-bound (the 12-tap PCF). **Both whole-frame and per-pass timers read from a query *ring*
  harvested newest-ready, not a 1-frame ping-pong** — with vsync off the CPU runs several frames ahead, so a
  1-frame read would perpetually see "not available" and freeze the timers stale. ⚠️ **With vsync on, read
  `compMs` only from a vsync-off capture** — the composition pass (first to write the default framebuffer)
  absorbs swapchain back-pressure, so `compMs` reads a vsync-quantized stall, not real fill. When `updateMs`
  is large, the srv/net/cli splits localize it. `updCalls` ≫ 1 means updates are running behind and being
  batched. **The profiler reads only lock-free mirrors** (`volatile`/`Interlocked` depth fields), never
  `ConcurrentDictionary`/`ConcurrentQueue.Count` or a `_meshLock` take, so recording doesn't contend with the
  apply/mesh threads and inflate the very stutter it measures.

  **F10 also drives a second CSV split by *grain*** (a per-frame time-series vs a per-chunk latency log).
  `chunk-trace.csv` (`Util/ChunkTracer.cs`, same F10 toggle, same `t` clock so they correlate offline) is
  **one row per chunk**, emitted on upload finish, keyed by chunk position. Its schema is a **work-vs-wait
  decomposition** of the chunk's whole life across the 4 pipeline threads — columns `t, posX/Y/Z, source
  (disk/gen/edit/stream), mp`, then the tiling spans `genMs`(work) `stageWaitMs` `drainWaitMs` `streamWaitMs`
  `netWaitMs` `applyWaitMs` `applyMs`(work) `meshWaitMs` `meshMs`(work) `uploadWaitMs`, and `totalMs`.
  Adjacent stamps tile the timeline, so in SP the spans sum to `totalMs` (an instrumentation self-check). A
  **multiplayer client** has no in-process server, so server stages are blank and `totalMs` starts at
  `applyWaitMs` (`mp=1`, `source=stream`); a **block-edit** (or a re-stream after eviction) emits a separate
  `source=edit` row covering only the mesh→upload tail. Memory is bounded by a `MaxLive` cap + `Abandon` at
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

**GPU frame capture (RenderDoc).** For per-draw GPU timing, overdraw, shader/buffer inspection of the
deferred passes, capture a frame with RenderDoc. RenderDoc only hooks our GL context over **GLX (X11)** —
under native Wayland GLFW 3.4 makes an EGL context it can't capture. So `Program.Main` forces the GLFW X11
backend (`InitHintPlatform.Platform`) when launched under RenderDoc (auto-detected via `RENDERDOC_CAPOPTS`)
or when `MC3_FORCE_X11=1`; normal runs keep native Wayland. Build first, then launch the **native apphost**
(not `dotnet run`, which forks):

```
renderdoccmd capture -d <bin/Debug/net10.0> <bin/Debug/net10.0/MinecraftClone3>   # F12 in-world to snap
```

`GraphicsDebug` (`Client/Graphics/GraphicsDebug.cs`) emits `KHR_debug` groups + object labels so a capture is
navigable: the passes nest as **Shadow / Geometry → Opaque/Transparent/Overlays / ShadowResolve /
Composition** (RenderDoc shows per-group GPU time for free), and the G-buffer/shadow targets and shader
programs get names. Every call is a no-op unless `Enabled` (same detection, or `MC3_GL_DEBUG=1`). **A
depth-only pass being the GPU bottleneck means geometry/draw-call-bound, not fill/shader-bound** — the shadow
pass redraws all in-range opaque chunks from the sun's POV, so the fix is reducing geometry submitted (shorter
`ShadowDistance`, smaller `ShadowMapSize`), not shader work.

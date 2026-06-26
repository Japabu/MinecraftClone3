using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Networking;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Toggleable per-frame profiler (F3) that appends a CSV row each render frame so frame-time
    /// spikes can be correlated with GC activity, allocation rate, chunk/mesh-queue load, and
    /// chunk-border crossings. Output: <see cref="GamePaths.UserDataDir"/>/profiling.csv.
    /// </summary>
    public static class Profiler
    {
        public static bool Recording { get; private set; }

        /// <summary>Set by the active world state (singleplayer only) so the profiler can split the
        /// server Pump's per-tick cost into chunk streaming vs block-change flushing.</summary>
        public static ServerNetwork Network;

        /// <summary>Set by the active world state (singleplayer only) so the profiler can sample the
        /// server-side staging-queue depth (the chunks waiting for the main-thread drain).</summary>
        public static WorldServer Server;

        public static string OutputPath { get; private set; }

        /// <summary>Seconds since recording started, shared with <see cref="ChunkTracer"/> so the per-frame
        /// and per-chunk CSVs correlate on one clock. Zero when not recording.</summary>
        public static double ElapsedSeconds => _clock.Elapsed.TotalSeconds;

        private static StreamWriter _writer;
        private static readonly StringBuilder _row = new StringBuilder(384);
        private static readonly Stopwatch _clock = new Stopwatch();
        private static long _lastGen0, _lastGen1, _lastGen2, _lastAlloc;
        private static int _rowsSinceFlush;
        private static Vector3i _lastPlayerChunk;

        // Main-thread allocation attributed per phase, accumulated across the update ticks in a
        // render-frame interval and emitted (then reset) each Record. Measured by the world state.
        private static long _phServer, _phNetwork, _phClient;

        public static void AddServerAlloc(long bytes) => _phServer += bytes;
        public static void AddNetworkAlloc(long bytes) => _phNetwork += bytes;
        public static void AddClientAlloc(long bytes) => _phClient += bytes;

        // Main-thread update-phase wall-clock (ms), accumulated across the update ticks in a render-frame
        // interval and emitted (then reset) each Record — the top-level split of updateMs into the three
        // StateWorld.Update calls, so a spike is attributable to server sim / networking / client update.
        private static double _phServerMs, _phNetworkMs, _phClientMs;

        public static void AddServerTime(double ms) => _phServerMs += ms;
        public static void AddNetworkTime(double ms) => _phNetworkMs += ms;
        public static void AddClientTime(double ms) => _phClientMs += ms;

        // Background-thread allocation, attributed per worker (Interlocked: called off the main thread).
        private static long _bgLoad, _bgLight, _bgUnload, _bgMesh, _bgApply;

        public static void AddLoadAlloc(long bytes) => System.Threading.Interlocked.Add(ref _bgLoad, bytes);
        public static void AddLightAlloc(long bytes) => System.Threading.Interlocked.Add(ref _bgLight, bytes);
        public static void AddUnloadAlloc(long bytes) => System.Threading.Interlocked.Add(ref _bgUnload, bytes);
        public static void AddMeshAlloc(long bytes) => System.Threading.Interlocked.Add(ref _bgMesh, bytes);
        public static void AddApplyAlloc(long bytes) => System.Threading.Interlocked.Add(ref _bgApply, bytes);

        // Per-pipeline-stage wall-clock, attributed to the producing thread. The background stages store
        // raw Stopwatch.GetTimestamp() tick deltas via Interlocked (called off the main thread) and are
        // converted to ms in Record; the server stage-drain is main-thread. Measured unconditionally (a
        // GetTimestamp pair is cheap) like the alloc brackets, so no recording gate on the producers.
        private static long _tDisk, _tGen, _tApply, _tMesh;
        private static double _drainAddMs;

        public static void AddDiskTicks(long ticks) => System.Threading.Interlocked.Add(ref _tDisk, ticks);
        public static void AddGenTicks(long ticks) => System.Threading.Interlocked.Add(ref _tGen, ticks);
        public static void AddApplyTicks(long ticks) => System.Threading.Interlocked.Add(ref _tApply, ticks);
        public static void AddMeshTicks(long ticks) => System.Threading.Interlocked.Add(ref _tMesh, ticks);
        public static void AddDrainAddTime(double ms) => _drainAddMs += ms;

        // Per-pipeline-stage throughput counts per render-frame interval (chunks crossing each stage).
        // Background stages via Interlocked; the server stage-drain count is main-thread.
        private static long _cFromDisk, _cGenerated, _cApplied, _cMeshed, _cDrainedAdd;

        public static void IncFromDisk() => System.Threading.Interlocked.Increment(ref _cFromDisk);
        public static void IncGenerated() => System.Threading.Interlocked.Increment(ref _cGenerated);
        public static void IncApplied() => System.Threading.Interlocked.Increment(ref _cApplied);
        public static void IncMeshed() => System.Threading.Interlocked.Increment(ref _cMeshed);
        public static void AddDrainAddCount(long n) => _cDrainedAdd += n;

        public static void Toggle()
        {
            if (Recording) Stop();
            else Start();
        }

        public static void Start()
        {
            if (Recording) return;

            OutputPath = Path.Combine(GamePaths.UserDataDir, "profiling.csv");
            _writer = new StreamWriter(OutputPath, false);
            _writer.WriteLine("t,frameMs,fps,updateMs,renderMs,swapMs,gapMs,gpuMs,shadowMs,geomMs,compMs,updCalls,gen0,gen1,gen2," +
                              "dGen0,dGen1,dGen2,heapMB,allocMB,srvMB,netMB,cliMB,rndMB,loadMB,lightMB,unloadMB,meshMB,applyMB," +
                              "chunks,renderData,pendingMesh,entities,pcx,pcy,pcz,borderCross," +
                              "srvMs,netMs,cliMs,streamMs,flushMs,chStreamed,chDrained,chPkts," +
                              "pktMs,drainMs,upMs,evictMs,upChunks,upIndices,upQ," +
                              "diskMs,genMs,applyMs,meshMs,drainAddMs," +
                              "chFromDisk,chGenerated,chApplied,chMeshed,chDrainedAdd," +
                              "srvStageQ,applyQ,renderReadyQ,disposeQ,drawnChunks");

            _lastGen0 = GC.CollectionCount(0);
            _lastGen1 = GC.CollectionCount(1);
            _lastGen2 = GC.CollectionCount(2);
            _lastAlloc = GC.GetTotalAllocatedBytes();
            _lastPlayerChunk = Vector3i.Zero;
            _rowsSinceFlush = 0;

            // The phase accumulators run every Update tick regardless of recording, so without this the
            // first row dumps all phase time/allocation accumulated since process start (e.g. the initial
            // world-load Pump showing up as a multi-hundred-ms netMs spike). Zero them so row 1 is clean.
            _phServer = _phNetwork = _phClient = 0;
            _phServerMs = _phNetworkMs = _phClientMs = 0;
            System.Threading.Interlocked.Exchange(ref _bgLoad, 0);
            System.Threading.Interlocked.Exchange(ref _bgLight, 0);
            System.Threading.Interlocked.Exchange(ref _bgUnload, 0);
            System.Threading.Interlocked.Exchange(ref _bgMesh, 0);
            System.Threading.Interlocked.Exchange(ref _bgApply, 0);
            System.Threading.Interlocked.Exchange(ref _tDisk, 0);
            System.Threading.Interlocked.Exchange(ref _tGen, 0);
            System.Threading.Interlocked.Exchange(ref _tApply, 0);
            System.Threading.Interlocked.Exchange(ref _tMesh, 0);
            System.Threading.Interlocked.Exchange(ref _cFromDisk, 0);
            System.Threading.Interlocked.Exchange(ref _cGenerated, 0);
            System.Threading.Interlocked.Exchange(ref _cApplied, 0);
            System.Threading.Interlocked.Exchange(ref _cMeshed, 0);
            _drainAddMs = 0;
            _cDrainedAdd = 0;

            ChunkTracer.Start();

            _clock.Restart();
            Recording = true;
            Logger.Info($"Profiling started -> {OutputPath}");
        }

        public static void Stop()
        {
            if (!Recording) return;

            Recording = false;
            _clock.Stop();
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
            ChunkTracer.Stop();
            Logger.Info($"Profiling stopped -> {OutputPath}");
        }

        public static void Record(double frameSeconds, double updateMs, double renderMs, double swapMs,
            double gapMs, double gpuMs, int updateCalls, long renderAllocBytes, ClientFrameStats stats)
        {
            if (!Recording) return;

            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var alloc = GC.GetTotalAllocatedBytes();

            var dGen0 = gen0 - _lastGen0;
            var dGen1 = gen1 - _lastGen1;
            var dGen2 = gen2 - _lastGen2;
            var allocMB = (alloc - _lastAlloc) / (1024.0 * 1024.0);

            _lastGen0 = gen0;
            _lastGen1 = gen1;
            _lastGen2 = gen2;
            _lastAlloc = alloc;

            var heapMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

            var chunk = stats.PlayerChunk;
            var borderCross = chunk != _lastPlayerChunk ? 1 : 0;
            _lastPlayerChunk = chunk;

            var fps = frameSeconds > 0 ? 1.0 / frameSeconds : 0;

            const double mb = 1024.0 * 1024.0;
            _row.Clear();
            Field(_clock.Elapsed.TotalSeconds, "0.000");
            Field(frameSeconds * 1000, "0.00");
            Field(fps, "0");
            Field(updateMs, "0.00");
            Field(renderMs, "0.00");
            Field(swapMs, "0.00");
            Field(gapMs, "0.00");
            Field(gpuMs, "0.00");
            Field(stats.ShadowMs, "0.00");
            Field(stats.GeometryMs, "0.00");
            Field(stats.CompositionMs, "0.00");
            Field((long) updateCalls);
            Field(gen0);
            Field(gen1);
            Field(gen2);
            Field(dGen0);
            Field(dGen1);
            Field(dGen2);
            Field(heapMB, "0.0");
            Field(allocMB, "0.000");
            Field(_phServer / mb, "0.000");
            Field(_phNetwork / mb, "0.000");
            Field(_phClient / mb, "0.000");
            Field(renderAllocBytes / mb, "0.000");
            Field(System.Threading.Interlocked.Exchange(ref _bgLoad, 0) / mb, "0.000");
            Field(System.Threading.Interlocked.Exchange(ref _bgLight, 0) / mb, "0.000");
            Field(System.Threading.Interlocked.Exchange(ref _bgUnload, 0) / mb, "0.000");
            Field(System.Threading.Interlocked.Exchange(ref _bgMesh, 0) / mb, "0.000");
            Field(System.Threading.Interlocked.Exchange(ref _bgApply, 0) / mb, "0.000");
            Field(stats.LoadedChunkCount);
            Field(stats.RenderListCount);
            Field(stats.MeshQueueDepth);
            Field(stats.EntityCount);
            Field(chunk.X);
            Field(chunk.Y);
            Field(chunk.Z);
            Field(borderCross);
            Field(_phServerMs, "0.00");
            Field(_phNetworkMs, "0.00");
            Field(_phClientMs, "0.00");
            Field(Network?.LastStreamMs ?? 0, "0.00");
            Field(Network?.LastFlushMs ?? 0, "0.00");
            Field((long) (Network?.LastChunksStreamed ?? 0));
            Field((long) (Network?.LastChangesDrained ?? 0));
            Field((long) (Network?.LastChangesPackets ?? 0));
            Field(stats.LastPacketMs, "0.00");
            Field(stats.LastDrainMs, "0.00");
            Field(stats.LastUploadMs, "0.00");
            Field(stats.LastEvictMs, "0.00");
            Field(stats.LastUploadChunks);
            Field(stats.LastUploadIndices);
            Field(stats.UploadQueueDepth);
            var msPerTick = 1000.0 / Stopwatch.Frequency;
            Field(System.Threading.Interlocked.Exchange(ref _tDisk, 0) * msPerTick, "0.00");
            Field(System.Threading.Interlocked.Exchange(ref _tGen, 0) * msPerTick, "0.00");
            Field(System.Threading.Interlocked.Exchange(ref _tApply, 0) * msPerTick, "0.00");
            Field(System.Threading.Interlocked.Exchange(ref _tMesh, 0) * msPerTick, "0.00");
            Field(_drainAddMs, "0.00");
            Field(System.Threading.Interlocked.Exchange(ref _cFromDisk, 0));
            Field(System.Threading.Interlocked.Exchange(ref _cGenerated, 0));
            Field(System.Threading.Interlocked.Exchange(ref _cApplied, 0));
            Field(System.Threading.Interlocked.Exchange(ref _cMeshed, 0));
            Field(_cDrainedAdd);
            Field((long) (Server?.StageQueueDepth ?? 0));
            Field(stats.ApplyQueueDepth);
            Field(stats.RenderReadyQueueDepth);
            Field(stats.DisposeQueueDepth);
            Field(stats.DrawnChunks);
            _writer.WriteLine(_row);

            _phServer = 0;
            _phNetwork = 0;
            _phClient = 0;
            _phServerMs = 0;
            _phNetworkMs = 0;
            _phClientMs = 0;
            _drainAddMs = 0;
            _cDrainedAdd = 0;

            if (++_rowsSinceFlush >= 120)
            {
                _writer.Flush();
                _rowsSinceFlush = 0;
            }
        }

        private static void Field(double value, string format)
        {
            Span<char> buffer = stackalloc char[32];
            value.TryFormat(buffer, out var written, format, CultureInfo.InvariantCulture);
            if (_row.Length > 0) _row.Append(',');
            _row.Append(buffer.Slice(0, written));
        }

        private static void Field(long value)
        {
            Span<char> buffer = stackalloc char[32];
            value.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture);
            if (_row.Length > 0) _row.Append(',');
            _row.Append(buffer.Slice(0, written));
        }
    }
}

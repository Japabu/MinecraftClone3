using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.IO;
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

        /// <summary>Set by the active world state so the profiler can sample chunk/entity counts.</summary>
        public static WorldClient World;

        public static string OutputPath { get; private set; }

        private static StreamWriter _writer;
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

        // Background-thread allocation, attributed per worker (Interlocked: called off the main thread).
        private static long _bgLoad, _bgLight, _bgUnload, _bgMesh;

        public static void AddLoadAlloc(long bytes) => System.Threading.Interlocked.Add(ref _bgLoad, bytes);
        public static void AddLightAlloc(long bytes) => System.Threading.Interlocked.Add(ref _bgLight, bytes);
        public static void AddUnloadAlloc(long bytes) => System.Threading.Interlocked.Add(ref _bgUnload, bytes);
        public static void AddMeshAlloc(long bytes) => System.Threading.Interlocked.Add(ref _bgMesh, bytes);

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
            _writer.WriteLine("t,frameMs,fps,updateMs,renderMs,gen0,gen1,gen2,dGen0,dGen1,dGen2," +
                              "heapMB,allocMB,srvMB,netMB,cliMB,rndMB,loadMB,lightMB,unloadMB,meshMB," +
                              "chunks,renderData,pendingMesh,entities,pcx,pcy,pcz,borderCross");

            _lastGen0 = GC.CollectionCount(0);
            _lastGen1 = GC.CollectionCount(1);
            _lastGen2 = GC.CollectionCount(2);
            _lastAlloc = GC.GetTotalAllocatedBytes();
            _lastPlayerChunk = PlayerChunk();
            _rowsSinceFlush = 0;

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
            Logger.Info($"Profiling stopped -> {OutputPath}");
        }

        public static void Record(double frameSeconds, double updateMs, double renderMs, long renderAllocBytes)
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

            var chunk = PlayerChunk();
            var borderCross = chunk != _lastPlayerChunk ? 1 : 0;
            _lastPlayerChunk = chunk;

            var fps = frameSeconds > 0 ? 1.0 / frameSeconds : 0;

            _writer.WriteLine(string.Join(",",
                F(_clock.Elapsed.TotalSeconds, "0.000"),
                F(frameSeconds * 1000, "0.00"),
                F(fps, "0"),
                F(updateMs, "0.00"),
                F(renderMs, "0.00"),
                gen0.ToString(CultureInfo.InvariantCulture),
                gen1.ToString(CultureInfo.InvariantCulture),
                gen2.ToString(CultureInfo.InvariantCulture),
                dGen0.ToString(CultureInfo.InvariantCulture),
                dGen1.ToString(CultureInfo.InvariantCulture),
                dGen2.ToString(CultureInfo.InvariantCulture),
                F(heapMB, "0.0"),
                F(allocMB, "0.000"),
                F(_phServer / (1024.0 * 1024.0), "0.000"),
                F(_phNetwork / (1024.0 * 1024.0), "0.000"),
                F(_phClient / (1024.0 * 1024.0), "0.000"),
                F(renderAllocBytes / (1024.0 * 1024.0), "0.000"),
                F(System.Threading.Interlocked.Exchange(ref _bgLoad, 0) / (1024.0 * 1024.0), "0.000"),
                F(System.Threading.Interlocked.Exchange(ref _bgLight, 0) / (1024.0 * 1024.0), "0.000"),
                F(System.Threading.Interlocked.Exchange(ref _bgUnload, 0) / (1024.0 * 1024.0), "0.000"),
                F(System.Threading.Interlocked.Exchange(ref _bgMesh, 0) / (1024.0 * 1024.0), "0.000"),
                (World?.LoadedChunks.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                (World?.RenderData.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                (World?.PendingMeshCount ?? 0).ToString(CultureInfo.InvariantCulture),
                (World?.Entities.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                chunk.X.ToString(CultureInfo.InvariantCulture),
                chunk.Y.ToString(CultureInfo.InvariantCulture),
                chunk.Z.ToString(CultureInfo.InvariantCulture),
                borderCross.ToString(CultureInfo.InvariantCulture)));

            _phServer = 0;
            _phNetwork = 0;
            _phClient = 0;

            if (++_rowsSinceFlush >= 120)
            {
                _writer.Flush();
                _rowsSinceFlush = 0;
            }
        }

        private static Vector3i PlayerChunk()
        {
            var entity = PlayerController.PlayerEntity;
            return entity == null ? Vector3i.Zero : WorldBase.ChunkInWorld(entity.Position.ToVector3i());
        }

        private static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
    }
}

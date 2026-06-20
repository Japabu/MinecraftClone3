using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using MinecraftClone3API.IO;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    /// <summary>How a chunk entered the pipeline, written to the <c>source</c> column.</summary>
    public enum ChunkSource
    {
        None = 0,
        Disk = 1,
        Gen = 2,
        Edit = 3
    }

    /// <summary>
    /// Per-chunk lifecycle tracer — the per-entity companion to the per-frame <see cref="Profiler"/>.
    /// Both are driven by the same F10 toggle (<see cref="Profiler.Start"/>/<see cref="Profiler.Stop"/>
    /// call into here) and share <see cref="Profiler.ElapsedSeconds"/> so the two CSVs correlate offline.
    ///
    /// A chunk's life spans many frames and four threads (load → main drain → stream → client decode →
    /// mesh → GL upload), so its latency can't be frame-bucketed; instead a side
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by chunk position (the only identity stable
    /// across the <c>CachedChunk → Chunk → ChunkRenderData</c> handoffs) accumulates timestamps, and one
    /// row is emitted to <c>chunk-trace.csv</c> when the chunk finishes uploading.
    ///
    /// The schema is a <b>work-vs-wait decomposition</b>: adjacent stamps tile the timeline, so in
    /// singleplayer the per-stage spans sum to <c>totalMs</c> (an instrumentation self-check). Multiplayer
    /// clients have no in-process server, so the server stages (<c>genMs</c>..<c>netWaitMs</c>) are blank
    /// and <c>totalMs</c> starts at <c>applyWaitMs</c>; block-edit remeshes emit a separate
    /// <c>source=edit</c> row covering only the mesh→upload tail.
    /// </summary>
    public static class ChunkTracer
    {
        /// <summary>Set by the world state. Drives the <c>mp</c> column and the emission-start rule (server
        /// stamps never fire on a multiplayer client, where the server runs in a different process).</summary>
        public static bool Multiplayer;

        public static string OutputPath { get; private set; }

        // Worst case ~1 MB (struct ~96 B). A burst that exceeds it drops *new* traces (counted) rather
        // than unbounded growth; the explicit Abandon calls + TTL sweep normally keep it far below.
        private const int MaxLive = 8192;
        private const double TtlSeconds = 30;
        private const int SweepInterval = 512;

        private struct ChunkTrace
        {
            public long TFirst;
            public long TBorn, TLoaded, TStaged, TPublished, TStreamed;
            public long TApplyEnq, TApplyStart, TApplied, TMeshStart, TMeshDone, TUploaded;
            public ChunkSource Source;
        }

        private enum Stage
        {
            Born, Loaded, Staged, Published, Streamed,
            ApplyEnq, ApplyStart, Applied, EditApplied, MeshStart, MeshDone
        }

        private static readonly ConcurrentDictionary<Vector3i, ChunkTrace> _live =
            new ConcurrentDictionary<Vector3i, ChunkTrace>();
        private static int _liveCount;
        private static long _dropped;

        private static StreamWriter _writer;
        private static readonly StringBuilder _row = new StringBuilder(192);
        private static int _rowsSinceFlush;
        private static int _uploadsSinceSweep;

        public static void Start()
        {
            OutputPath = Path.Combine(GamePaths.UserDataDir, "chunk-trace.csv");
            _writer = new StreamWriter(OutputPath, false);
            _writer.WriteLine("t,posX,posY,posZ,source,mp,genMs,stageWaitMs,drainWaitMs,streamWaitMs,netWaitMs," +
                              "applyWaitMs,applyMs,meshWaitMs,meshMs,uploadWaitMs,totalMs");

            _live.Clear();
            Interlocked.Exchange(ref _liveCount, 0);
            _dropped = 0;
            _rowsSinceFlush = 0;
            _uploadsSinceSweep = 0;
        }

        public static void Stop()
        {
            if (_writer == null) return;

            _writer.Flush();
            _writer.Dispose();
            _writer = null;

            if (_dropped > 0)
                Logger.Warn($"ChunkTracer dropped {_dropped} traces (live cap {MaxLive})");

            _live.Clear();
            Interlocked.Exchange(ref _liveCount, 0);
        }

        public static void Born(Vector3i pos) => Mark(pos, Stage.Born, ChunkSource.None, true);
        public static void Loaded(Vector3i pos, ChunkSource source) => Mark(pos, Stage.Loaded, source, false);
        public static void Staged(Vector3i pos) => Mark(pos, Stage.Staged, ChunkSource.None, false);
        public static void Published(Vector3i pos) => Mark(pos, Stage.Published, ChunkSource.None, false);
        public static void Streamed(Vector3i pos) => Mark(pos, Stage.Streamed, ChunkSource.None, false);
        public static void ApplyEnq(Vector3i pos) => Mark(pos, Stage.ApplyEnq, ChunkSource.None, true);
        public static void ApplyStart(Vector3i pos) => Mark(pos, Stage.ApplyStart, ChunkSource.None, false);
        public static void Applied(Vector3i pos) => Mark(pos, Stage.Applied, ChunkSource.None, false);
        public static void EditApplied(Vector3i pos) => Mark(pos, Stage.EditApplied, ChunkSource.Edit, true);
        public static void MeshStart(Vector3i pos) => Mark(pos, Stage.MeshStart, ChunkSource.None, false);
        public static void MeshDone(Vector3i pos) => Mark(pos, Stage.MeshDone, ChunkSource.None, false);

        /// <summary>Final stamp: records the upload time, removes the trace, and writes its row. Main-thread
        /// only (the GL upload site), so writer access never races the background stamps (which only touch
        /// the thread-safe <see cref="_live"/> map).</summary>
        public static void Uploaded(Vector3i pos)
        {
            if (!Profiler.Recording) return;
            var t = Stopwatch.GetTimestamp();

            if (!_live.TryRemove(pos, out var c)) return;
            Interlocked.Decrement(ref _liveCount);

            c.TUploaded = t;
            Emit(pos, c);
            MaybeSweep(t);
        }

        /// <summary>Drops a trace whose chunk left the pipeline before uploading (an empty chunk never
        /// staged, a server-side eviction, or a client cache eviction — the last also clears the old trace
        /// so a re-streamed chunk starts a fresh one).</summary>
        public static void Abandon(Vector3i pos)
        {
            if (!Profiler.Recording) return;
            if (_live.TryRemove(pos, out _)) Interlocked.Decrement(ref _liveCount);
        }

        private static void Mark(Vector3i pos, Stage stage, ChunkSource source, bool create)
        {
            if (!Profiler.Recording) return;
            var t = Stopwatch.GetTimestamp();

            var existed = _live.TryGetValue(pos, out var c);
            if (!existed)
            {
                if (!create) return;
                if (_liveCount >= MaxLive)
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }

                c = default;
                c.TFirst = t;
            }

            switch (stage)
            {
                case Stage.Born: c.TBorn = t; break;
                case Stage.Loaded: c.TLoaded = t; c.Source = source; break;
                case Stage.Staged: c.TStaged = t; break;
                case Stage.Published: c.TPublished = t; break;
                case Stage.Streamed: c.TStreamed = t; break;
                case Stage.ApplyEnq: c.TApplyEnq = t; break;
                case Stage.ApplyStart: c.TApplyStart = t; break;
                case Stage.Applied: c.TApplied = t; break;
                case Stage.EditApplied: c.TApplied = t; c.Source = ChunkSource.Edit; break;
                case Stage.MeshStart: c.TMeshStart = t; break;
                case Stage.MeshDone: c.TMeshDone = t; break;
            }

            _live[pos] = c;
            if (!existed) Interlocked.Increment(ref _liveCount);
        }

        private static void Emit(Vector3i pos, ChunkTrace c)
        {
            if (_writer == null) return;

            long start;
            if (c.Source == ChunkSource.Edit)
            {
                if (c.TApplied == 0) return;
                start = c.TApplied;
            }
            else if (Multiplayer)
            {
                if (c.TApplyEnq == 0) return;
                start = c.TApplyEnq;
            }
            else
            {
                if (c.TBorn == 0) return;
                start = c.TBorn;
            }

            _row.Clear();
            FieldNum(Profiler.ElapsedSeconds, "0.000");
            FieldNum(pos.X);
            FieldNum(pos.Y);
            FieldNum(pos.Z);
            FieldText(SourceText(c.Source));
            FieldNum(Multiplayer ? 1L : 0L);
            Delta(c.TBorn, c.TLoaded);
            Delta(c.TLoaded, c.TStaged);
            Delta(c.TStaged, c.TPublished);
            Delta(c.TPublished, c.TStreamed);
            Delta(c.TStreamed, c.TApplyEnq);
            Delta(c.TApplyEnq, c.TApplyStart);
            Delta(c.TApplyStart, c.TApplied);
            Delta(c.TApplied, c.TMeshStart);
            Delta(c.TMeshStart, c.TMeshDone);
            Delta(c.TMeshDone, c.TUploaded);
            Delta(start, c.TUploaded);
            _writer.WriteLine(_row);

            if (++_rowsSinceFlush >= 120)
            {
                _writer.Flush();
                _rowsSinceFlush = 0;
            }
        }

        // Safety net for traces that never reach Uploaded or Abandon (e.g. a chunk streamed but the client
        // session dropped). Runs on the main thread (from Uploaded) every SweepInterval emits.
        private static void MaybeSweep(long now)
        {
            if (++_uploadsSinceSweep < SweepInterval) return;
            _uploadsSinceSweep = 0;

            var ttlTicks = (long) (TtlSeconds * Stopwatch.Frequency);
            foreach (var kvp in _live)
            {
                if (now - kvp.Value.TFirst <= ttlTicks) continue;
                if (_live.TryRemove(kvp.Key, out _)) Interlocked.Decrement(ref _liveCount);
            }
        }

        private static string SourceText(ChunkSource s)
        {
            switch (s)
            {
                case ChunkSource.Disk: return "disk";
                case ChunkSource.Gen: return "gen";
                case ChunkSource.Edit: return "edit";
                default: return "stream";
            }
        }

        private static void Delta(long t0, long t1)
        {
            if (_row.Length > 0) _row.Append(',');
            if (t0 == 0 || t1 == 0 || t1 < t0) return;

            var ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            Span<char> buffer = stackalloc char[32];
            ms.TryFormat(buffer, out var written, "0.000", CultureInfo.InvariantCulture);
            _row.Append(buffer.Slice(0, written));
        }

        private static void FieldText(string value)
        {
            if (_row.Length > 0) _row.Append(',');
            _row.Append(value);
        }

        private static void FieldNum(double value, string format)
        {
            Span<char> buffer = stackalloc char[32];
            value.TryFormat(buffer, out var written, format, CultureInfo.InvariantCulture);
            if (_row.Length > 0) _row.Append(',');
            _row.Append(buffer.Slice(0, written));
        }

        private static void FieldNum(long value)
        {
            Span<char> buffer = stackalloc char[32];
            value.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture);
            if (_row.Length > 0) _row.Append(',');
            _row.Append(buffer.Slice(0, written));
        }
    }
}

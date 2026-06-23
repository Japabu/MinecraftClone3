using System.Collections.Generic;
using System.Threading;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Blocks
{
    /// <summary>
    /// The region store for Phase-2 LOD columns. Keyed by region position (<see cref="LodColumn.RegionKey"/>).
    /// Has exactly ONE writer thread (server: the LOD generation thread; client: the apply thread), so it
    /// satisfies the per-container single-writer invariant. Readers (server streaming on the tick thread, the
    /// client mesh + main threads, the profiler) take the coarse lock — contention is negligible because the
    /// writer publishes a whole region at once (background cadence) and never mutates a published region in
    /// place. <see cref="RegionCount"/> is an <see cref="Interlocked"/>-maintained mirror so the profiler reads
    /// it lock-free.
    /// </summary>
    public class LodColumnStore
    {
        private readonly Dictionary<Vector3i, LodColumn> _regions = new Dictionary<Vector3i, LodColumn>();
        private readonly object _lock = new object();
        private int _regionCount;

        public int RegionCount => _regionCount;

        public bool TryGetRegion(Vector3i regionKey, out LodColumn region)
        {
            lock (_lock) return _regions.TryGetValue(regionKey, out region);
        }

        public bool HasRegion(Vector3i regionKey)
        {
            lock (_lock) return _regions.ContainsKey(regionKey);
        }

        /// <summary>Publishes a filled region (replacing any prior one — never mutates a published array).</summary>
        public void PutRegion(LodColumn region)
        {
            lock (_lock)
            {
                if (!_regions.ContainsKey(region.Position)) Interlocked.Increment(ref _regionCount);
                _regions[region.Position] = region;
            }
        }

        public void RemoveRegion(Vector3i regionKey)
        {
            lock (_lock)
                if (_regions.Remove(regionKey)) Interlocked.Decrement(ref _regionCount);
        }

        /// <summary>Snapshots the current region keys into <paramref name="into"/> (reused scratch) for a
        /// streaming/eviction scan without holding the lock across the scan.</summary>
        public void SnapshotKeys(List<Vector3i> into)
        {
            into.Clear();
            lock (_lock)
                foreach (var key in _regions.Keys) into.Add(key);
        }

        public void Clear()
        {
            lock (_lock)
            {
                _regions.Clear();
                Interlocked.Exchange(ref _regionCount, 0);
            }
        }
    }
}

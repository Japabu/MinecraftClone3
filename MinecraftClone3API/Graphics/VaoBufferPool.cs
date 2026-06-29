using System.Collections.Generic;
using System.Collections.Concurrent;
using Silk.NET.Maths;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Recycles the CPU-side vertex buffer lists between chunk remeshes. The mesh thread rents a set
    /// of lists in <see cref="VertexArrayObject.Add"/> (first vertex) and returns them in
    /// <see cref="VertexArrayObject.Clear"/> once the data has been uploaded to the GPU on the main
    /// thread; distinct chunks mesh and upload concurrently, so the bags are thread-safe. Without
    /// this, every remesh would new six capacity-1024 lists — the mesh thread's steady-state allocator
    /// under editing and streaming.
    /// </summary>
    internal static class VaoBufferPool
    {
        // Pre-sized to a typical surface chunk's vertex count so a freshly-allocated burst list (when the pool
        // is drained under heavy streaming) doesn't grow 1024→2048→…→8192, discarding every intermediate array;
        // that doubling churn is a mesh-thread per-burst allocation spike.
        private const int InitialCapacity = 6144;

        private static readonly ConcurrentBag<List<Vector3D<float>>> Vector3Lists = new ConcurrentBag<List<Vector3D<float>>>();
        private static readonly ConcurrentBag<List<Vector2D<float>>> Vector2Lists = new ConcurrentBag<List<Vector2D<float>>>();
        private static readonly ConcurrentBag<List<uint>> UintLists = new ConcurrentBag<List<uint>>();

        public static List<Vector3D<float>> RentVector3() => Vector3Lists.TryTake(out var list) ? list : new List<Vector3D<float>>(InitialCapacity);
        public static List<Vector2D<float>> RentVector2() => Vector2Lists.TryTake(out var list) ? list : new List<Vector2D<float>>(InitialCapacity);
        public static List<uint> RentUint() => UintLists.TryTake(out var list) ? list : new List<uint>(InitialCapacity);

        public static void Return(List<Vector3D<float>> list)
        {
            if (list == null) return;
            list.Clear();
            Vector3Lists.Add(list);
        }

        public static void Return(List<Vector2D<float>> list)
        {
            if (list == null) return;
            list.Clear();
            Vector2Lists.Add(list);
        }

        public static void Return(List<uint> list)
        {
            if (list == null) return;
            list.Clear();
            UintLists.Add(list);
        }
    }
}

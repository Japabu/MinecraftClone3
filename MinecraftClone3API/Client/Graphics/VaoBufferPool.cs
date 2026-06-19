using System.Collections.Generic;
using System.Collections.Concurrent;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Recycles the CPU-side vertex buffer lists between chunk remeshes. The mesh thread rents a set
    /// of lists in <see cref="VertexArrayObject.Add"/> (first vertex) and returns them in
    /// <see cref="VertexArrayObject.Clear"/> once the data has been uploaded to the GPU on the main
    /// thread; distinct chunks mesh and upload concurrently, so the bags are thread-safe. Without
    /// this, every remesh newed six capacity-1024 lists, which a trace flagged as the mesh thread's
    /// steady-state allocator under editing and streaming.
    /// </summary>
    internal static class VaoBufferPool
    {
        private const int InitialCapacity = 1024;

        private static readonly ConcurrentBag<List<Vector3>> Vector3Lists = new ConcurrentBag<List<Vector3>>();
        private static readonly ConcurrentBag<List<Vector4>> Vector4Lists = new ConcurrentBag<List<Vector4>>();
        private static readonly ConcurrentBag<List<uint>> UintLists = new ConcurrentBag<List<uint>>();

        public static List<Vector3> RentVector3() => Vector3Lists.TryTake(out var list) ? list : new List<Vector3>(InitialCapacity);
        public static List<Vector4> RentVector4() => Vector4Lists.TryTake(out var list) ? list : new List<Vector4>(InitialCapacity);
        public static List<uint> RentUint() => UintLists.TryTake(out var list) ? list : new List<uint>(InitialCapacity);

        public static void Return(List<Vector3> list)
        {
            if (list == null) return;
            list.Clear();
            Vector3Lists.Add(list);
        }

        public static void Return(List<Vector4> list)
        {
            if (list == null) return;
            list.Clear();
            Vector4Lists.Add(list);
        }

        public static void Return(List<uint> list)
        {
            if (list == null) return;
            list.Clear();
            UintLists.Add(list);
        }
    }
}

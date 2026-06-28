using Silk.NET.Maths;

namespace MinecraftClone3API.Util
{
    /// <summary>Per-frame client-side samples the render loop pushes into <see cref="Profiler.Record"/>.
    /// Lets <see cref="Profiler"/> stay GPU-free in Core while still logging client renderer/world stats:
    /// the client (which owns <c>WorldClient</c>, <c>RenderDebug</c>, <c>GpuTimers</c>) fills this each frame
    /// so Core never reaches into client types. All fields default to 0 when there is no active world.</summary>
    public struct ClientFrameStats
    {
        public Vector3D<int> PlayerChunk;

        public int LoadedChunkCount;
        public int RenderListCount;
        public int MeshQueueDepth;
        public int EntityCount;

        public double LastPacketMs;
        public double LastDrainMs;
        public double LastUploadMs;
        public double LastEvictMs;
        public long LastUploadChunks;
        public long LastUploadIndices;
        public long UploadQueueDepth;

        public long ApplyQueueDepth;
        public long RenderReadyQueueDepth;
        public long DisposeQueueDepth;

        public long DrawnChunks;

        public double ShadowMs;
        public double GeometryMs;
        public double CompositionMs;
    }
}

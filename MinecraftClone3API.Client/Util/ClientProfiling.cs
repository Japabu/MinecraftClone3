using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using Silk.NET.Maths;

namespace MinecraftClone3API.Util
{
    /// <summary>Client-side companion to the GPU-free Core <see cref="Profiler"/>: holds the active
    /// <see cref="WorldClient"/> and samples the client renderer/world each frame into a
    /// <see cref="ClientFrameStats"/> the render loop pushes into <see cref="Profiler.Record"/>. Keeps the
    /// WorldClient/RenderDebug/GpuTimers/PlayerController reads on the client side of the assembly split.</summary>
    public static class ClientProfiling
    {
        /// <summary>Set by the active world state so the profiler can sample chunk/entity counts.</summary>
        public static WorldClient World;

        public static ClientFrameStats SampleFrame()
        {
            var stats = new ClientFrameStats();

            var entity = PlayerController.PlayerEntity;
            stats.PlayerChunk = entity == null
                ? Vector3D<int>.Zero
                : WorldBase.ChunkInWorld(entity.Position.ToVector3i());

            stats.ShadowMs = GpuTimers.Ms(GpuTimers.Pass.Shadow);
            stats.GeometryMs = GpuTimers.Ms(GpuTimers.Pass.Geometry);
            stats.CompositionMs = GpuTimers.Ms(GpuTimers.Pass.Composition);
            stats.DrawnChunks = RenderDebug.DrawnChunks;

            var w = World;
            if (w != null)
            {
                stats.LoadedChunkCount = w.LoadedChunkCount;
                stats.RenderListCount = w.RenderList.Count;
                stats.MeshQueueDepth = w.MeshQueueDepth;
                stats.EntityCount = w.Entities.Count;
                stats.LastPacketMs = w.LastPacketMs;
                stats.LastDrainMs = w.LastDrainMs;
                stats.LastUploadMs = w.LastUploadMs;
                stats.LastEvictMs = w.LastEvictMs;
                stats.LastUploadChunks = w.LastUploadChunks;
                stats.LastUploadIndices = w.LastUploadIndices;
                stats.UploadQueueDepth = w.UploadQueueDepth;
                stats.ApplyQueueDepth = w.ApplyQueueDepth;
                stats.RenderReadyQueueDepth = w.RenderReadyQueueDepth;
                stats.DisposeQueueDepth = w.DisposeQueueDepth;
            }

            return stats;
        }
    }
}

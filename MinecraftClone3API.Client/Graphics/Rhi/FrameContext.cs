using System;
using Silk.NET.WebGPU;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// Drives one presented frame: acquire the swapchain texture, hand out the per-frame command encoder,
    /// then submit + present. The renderer asks for <see cref="SurfaceView"/> to write the final tonemapped
    /// image into the swapchain, records everything through <see cref="Encoder"/>, and calls
    /// <see cref="Present"/> to finish.
    /// </summary>
    public sealed unsafe class FrameContext : IDisposable
    {
        public GpuCommandEncoder Encoder { get; private set; }
        public TextureView* SurfaceView { get; private set; }
        private Silk.NET.WebGPU.Texture* _surfaceTexture;
        private bool _skip;

        /// <summary>Begin a frame. Returns false if the swapchain texture could not be acquired (e.g. a resize
        /// mid-flight) — the caller should skip rendering this frame.</summary>
        public bool Begin()
        {
            _skip = false;
            var surfaceTex = new SurfaceTexture();
            Gpu.Api.SurfaceGetCurrentTexture(Gpu.Context.Surface, ref surfaceTex);
            switch (surfaceTex.Status)
            {
                case SurfaceGetCurrentTextureStatus.Success:
                    break;
                case SurfaceGetCurrentTextureStatus.Timeout:
                case SurfaceGetCurrentTextureStatus.Outdated:
                case SurfaceGetCurrentTextureStatus.Lost:
                    if (surfaceTex.Texture != null) Gpu.Api.TextureRelease(surfaceTex.Texture);
                    _skip = true;
                    return false;
                default:
                    Logger.Error($"wgpu: surface texture acquire failed: {surfaceTex.Status}");
                    _skip = true;
                    return false;
            }

            _surfaceTexture = surfaceTex.Texture;
            SurfaceView = Gpu.Api.TextureCreateView(_surfaceTexture, null);
            Encoder = GpuCommandEncoder.Create("frame");
            return true;
        }

        /// <summary>Finish the encoder, submit to the queue, and present the swapchain image.</summary>
        public void Present()
        {
            if (_skip) return;
            var cmd = Encoder.Finish("frame");
            var cmdLocal = cmd;
            Gpu.Api.QueueSubmit(Gpu.Queue, 1, &cmdLocal);
            Gpu.Api.SurfacePresent(Gpu.Context.Surface);

            Gpu.Api.CommandBufferRelease(cmd);
            Encoder.Release();
            if (SurfaceView != null) { Gpu.Api.TextureViewRelease(SurfaceView); SurfaceView = null; }
            if (_surfaceTexture != null) { Gpu.Api.TextureRelease(_surfaceTexture); _surfaceTexture = null; }
        }

        public void Dispose() { }
    }
}

using System;
using System.Diagnostics;
using MinecraftClone3API.Client;
using MinecraftClone3API.Graphics.Rhi;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// The per-frame conductor — the single place the WebGPU frame lifecycle lives. States record their draws
    /// into the frame command encoder rather than issuing them immediately against a default framebuffer.
    ///
    /// <para>Each frame: <see cref="BeginFrame"/> acquires the swapchain image and opens the frame command
    /// encoder; states then record into it. World rendering composes into the HDR scene target
    /// (<see cref="HdrScene"/>, rgba16float) and calls <see cref="MarkSceneRendered"/>. <see cref="EndFrame"/>
    /// opens the surface render pass, tonemaps the HDR scene into it (when a world drew) and flushes the GUI
    /// sprite batch over the top, then presents.</para>
    ///
    /// <para>Bind group 0 (the per-frame <see cref="FrameUniform"/>) is owned here and exposed as
    /// <see cref="FrameBindGroup"/> so every world pass shares one uniform write per frame.</para>
    /// </summary>
    public static unsafe class Renderer
    {
        private const string ShaderDir = "System/Shaders/";

        private static FrameContext _frame;

        private static GpuTexture _hdrScene;
        public static GpuTexture HdrScene => _hdrScene;
        public const TextureFormat HdrFormat = TextureFormat.Rgba16float;

        private static GpuBuffer _frameUbo;
        private static GpuBindGroup _frameBind;
        public static GpuBindGroup FrameBindGroup => _frameBind;

        /// <summary>The view/projection of the last <see cref="SetFrameUniform"/>, cached so overlay renderers
        /// that bake a full model-view-projection into a push constant (the block-outline / block-break passes,
        /// whose shaders carry no <see cref="FrameBindGroup"/>) can read the same matrices the Frame UBO holds.</summary>
        public static Matrix4 View { get; private set; }
        public static Matrix4 Projection { get; private set; }

        private static GpuShaderModule _tonemapModule;
        private static GpuPipelineLayout _tonemapLayout;
        private static GpuRenderPipeline _tonemapPipeline;
        private static GpuBindGroup _tonemapBind;

        /// <summary>Nether-portal screen-warp intensity (0..1), set by the world state each frame from the local
        /// player's portal soak. 0 leaves the present pass byte-identical to no warp.</summary>
        public static float PortalWarp;
        private static readonly Stopwatch _warpClock = Stopwatch.StartNew();

        private struct TonemapWarp
        {
            public float Intensity;
            public float Time;
        }

        private static bool _sceneRendered;

        public static int Width { get; private set; }
        public static int Height { get; private set; }

        private static PresentMode _presentMode = PresentMode.Fifo;
        private static bool _surfaceReady;

        /// <summary>Surface clear colour for frames with no world (menus paint their own background over it).</summary>
        public static Vector4 ClearColor = new Vector4(0f, 0f, 0f, 1f);

        public static GpuCommandEncoder Encoder => _frame.Encoder;
        public static TextureView* SurfaceView => _frame.SurfaceView;

        public static void Load(int width, int height, Func<string, string> readShader)
        {
            GpuLayouts.Load();
            GpuSamplers.Load();

            _frame = new FrameContext();

            _frameUbo = new GpuBuffer((ulong)sizeof(FrameUniform),
                BufferUsage.Uniform | BufferUsage.CopyDst, "frame");
            _frameBind = new GpuBindGroup(GpuLayouts.Frame, new[] { GpuBindGroup.Buffer(0, _frameUbo) }, "frame");

            _tonemapModule = new GpuShaderModule(readShader(ShaderDir + "Tonemap.wgsl"), "tonemap");
            _tonemapLayout = new GpuPipelineLayout(new[] { GpuPipelineLayout.Ptr(GpuLayouts.ScreenTexture) },
                ShaderStage.Fragment, (uint)sizeof(TonemapWarp), "tonemap");
            _tonemapPipeline = new GpuRenderPipeline(_tonemapLayout, _tonemapModule, "vs_main", "fs_main",
                ReadOnlySpan<VertexBufferDesc>.Empty,
                stackalloc[] { new ColorTargetDesc(Gpu.SurfaceFormat) }, depth: null, label: "tonemap");

            GuiBatch.Load(readShader(ShaderDir + "Sprite.wgsl"));

            _surfaceReady = true;
            Resize(width, height);
        }

        /// <summary>Set the swapchain present mode from the user's vsync preference and reconfigure the surface.</summary>
        public static void SetVSync(VSyncMode mode)
        {
            _presentMode = mode switch
            {
                VSyncMode.Off => PresentMode.Immediate,
                VSyncMode.Adaptive => PresentMode.Mailbox,
                _ => PresentMode.Fifo,
            };
            if (_surfaceReady) Gpu.Context.ConfigureSurface(Width, Height, _presentMode);
        }

        public static void Resize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            if (width == Width && height == Height && _hdrScene != null) return;
            Width = width;
            Height = height;

            if (_surfaceReady) Gpu.Context.ConfigureSurface(Width, Height, _presentMode);

            _hdrScene?.Dispose();
            // CopySrc so Screenshot can read the composed scene back (CommandEncoderCopyTextureToBuffer).
            _hdrScene = new GpuTexture((uint)width, (uint)height, HdrFormat,
                TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc, label: "HdrScene");

            _tonemapBind?.Dispose();
            _tonemapBind = new GpuBindGroup(GpuLayouts.ScreenTexture, new[]
            {
                GpuBindGroup.Texture(0, _hdrScene.View),
                GpuBindGroup.Sampler(1, GpuSamplers.Framebuffer),
            }, "tonemap");
        }

        /// <summary>Upload this frame's shared view/projection/camera uniform (bind group 0).</summary>
        public static void SetFrameUniform(in Matrix4 view, in Matrix4 projection, Vector3 cameraPos)
        {
            View = view;
            Projection = projection;
            var u = FrameUniform.From(view, projection, cameraPos);
            _frameUbo.QueueWriteStruct(u);
        }

        /// <summary>The world renderer calls this once it has composed into <see cref="HdrScene"/>, so
        /// <see cref="EndFrame"/> tonemaps it to the surface instead of just clearing.</summary>
        public static void MarkSceneRendered() => _sceneRendered = true;

        public static bool BeginFrame()
        {
            _sceneRendered = false;
            GuiBatch.Begin();
            return _frame.Begin();
        }

        public static void EndFrame()
        {
            var clear = ColorAttachment.ClearTo(SurfaceView, ClearColor.X, ClearColor.Y, ClearColor.Z, ClearColor.W);
            var pass = RenderPassBuilder.Begin(Encoder, stackalloc[] { clear });
            if (_sceneRendered)
            {
                pass.SetPipeline(_tonemapPipeline);
                pass.SetBindGroup(0, _tonemapBind);
                var warp = new TonemapWarp { Intensity = PortalWarp, Time = (float)_warpClock.Elapsed.TotalSeconds };
                pass.SetPushConstants(ShaderStage.Fragment, 0, in warp);
                pass.Draw(3);
            }
            GuiBatch.Flush(pass);
            pass.End();
            pass.Release();

            _frame.Present();
        }
    }
}

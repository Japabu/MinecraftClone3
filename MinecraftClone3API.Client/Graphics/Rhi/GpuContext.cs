using System;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;
using Silk.NET.Windowing;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// The root of the WebGPU device. Owns the instance, adapter, logical device, default queue, and the
    /// window surface, and exposes the negotiated feature set the renderer gates its modern paths on
    /// (multi-draw-indirect-count, push constants, timestamp queries). Constructed once from the Silk.NET
    /// window after it is created; everything else in <see cref="Rhi"/> reaches the device through the
    /// static <see cref="Gpu"/> facade rather than threading a handle everywhere.
    ///
    /// All <c>unsafe</c> Silk.NET interop in the renderer is confined to the <see cref="Rhi"/> namespace;
    /// this type is its entry point.
    /// </summary>
    public sealed unsafe class GpuContext : IDisposable
    {
        public WebGPU Api { get; }
        /// <summary>wgpu-native extension API (multi-draw-indirect, push constants, pipeline-statistics).</summary>
        public Wgpu Native { get; }
        public Instance* Instance { get; }
        public Adapter* Adapter { get; }
        public Device* Device { get; }
        public Queue* Queue { get; }
        public Surface* Surface { get; }

        /// <summary>The format the surface was configured with (the swapchain colour format).</summary>
        public TextureFormat SurfaceFormat { get; private set; }

        public int SurfaceWidth { get; private set; }
        public int SurfaceHeight { get; private set; }

        public GpuFeatures Features { get; }

        /// <summary>wgpu-native's <c>maxPushConstantSize</c> when push constants are supported, else 0.</summary>
        public uint MaxPushConstantSize { get; }

        private readonly PfnErrorCallback _uncapturedError;

        public GpuContext(IWindow window)
        {
            Api = WebGPU.GetApi();
            Native = new Wgpu(Api.Context);

            var instanceDesc = new InstanceDescriptor();
            Instance = Api.CreateInstance(in instanceDesc);
            if (Instance == null)
                throw new InvalidOperationException("wgpu: failed to create instance");

            Surface = window.CreateWebGPUSurface(Api, Instance);
            if (Surface == null)
                throw new InvalidOperationException("wgpu: failed to create surface from window");

            Adapter = RequestAdapter();
            var props = new AdapterProperties();
            Api.AdapterGetProperties(Adapter, ref props);
            Logger.Info($"wgpu adapter: {SilkMarshal.PtrToString((nint)props.Name)} " +
                        $"[{props.BackendType}, {props.AdapterType}]");

            Features = GpuFeatures.Detect(Api, Adapter);
            MaxPushConstantSize = Features.PushConstants ? QueryMaxPushConstantSize() : 0;

            Device = RequestDevice();
            Queue = Api.DeviceGetQueue(Device);
            if (Queue == null)
                throw new InvalidOperationException("wgpu: device has no default queue");

            _uncapturedError = PfnErrorCallback.From(OnUncapturedError);
            Api.DeviceSetUncapturedErrorCallback(Device, _uncapturedError, null);

            ConfigurePreferredFormat();
        }

        private Adapter* RequestAdapter()
        {
            Adapter* result = null;
            var options = new RequestAdapterOptions
            {
                CompatibleSurface = Surface,
                PowerPreference = PowerPreference.HighPerformance,
            };
            var callback = PfnRequestAdapterCallback.From((status, adapter, msg, _) =>
            {
                if (status == RequestAdapterStatus.Success) result = adapter;
                else Logger.Error($"wgpu adapter request failed: {status} {SilkMarshal.PtrToString((nint)msg)}");
            });
            Api.InstanceRequestAdapter(Instance, in options, callback, null);
            if (result == null)
                throw new InvalidOperationException("wgpu: no suitable adapter");
            return result;
        }

        private Device* RequestDevice()
        {
            var enabled = Features.BuildEnabledList(out var count);
            Device* result = null;
            try
            {
                // Push constants are a wgpu-native extension limit: enabling the feature is not enough — the
                // device must also be created with a non-zero maxPushConstantSize, chained on as
                // RequiredLimitsExtras. Without this the limit defaults to 0 and every push-constant pipeline
                // (GUI sprite / block outline / block-break) fails to create.
                var extras = new RequiredLimitsExtras
                {
                    Chain = new ChainedStruct { SType = (SType)NativeSType.STypeRequiredLimitsExtras },
                    Limits = new NativeLimits { MaxPushConstantSize = MaxPushConstantSize },
                };
                var limits = new RequiredLimits
                {
                    Limits = MakeRequiredLimits(),
                    NextInChain = MaxPushConstantSize > 0 ? (ChainedStruct*)&extras : null,
                };
                var descriptor = new DeviceDescriptor
                {
                    RequiredFeatureCount = count,
                    RequiredFeatures = enabled,
                    RequiredLimits = &limits,
                };
                var callback = PfnRequestDeviceCallback.From((status, device, msg, _) =>
                {
                    if (status == RequestDeviceStatus.Success) result = device;
                    else Logger.Error($"wgpu device request failed: {status} {SilkMarshal.PtrToString((nint)msg)}");
                });
                Api.AdapterRequestDevice(Adapter, in descriptor, callback, null);
            }
            finally
            {
                if (enabled != null) SilkMarshal.Free((nint)enabled);
            }
            if (result == null)
                throw new InvalidOperationException("wgpu: device request returned null");
            return result;
        }

        private Limits MakeRequiredLimits()
        {
            // Default to the adapter's reported limits so we never request more than is available; the
            // renderer's real needs (large storage buffers for chunk metadata, many bind groups) sit well
            // under any desktop backend's defaults.
            var supported = new SupportedLimits();
            Api.AdapterGetLimits(Adapter, ref supported);
            return supported.Limits;
        }

        private uint QueryMaxPushConstantSize()
        {
            return Features.QueryMaxPushConstantSize(Api, Adapter);
        }

        private void ConfigurePreferredFormat()
        {
            var caps = new SurfaceCapabilities();
            Api.SurfaceGetCapabilities(Surface, Adapter, ref caps);
            // Prefer a non-sRGB UNORM surface: the renderer tonemaps from an HDR target and applies its own
            // gamma, so an sRGB swapchain would double-encode. Fall back to the adapter's first format.
            var chosen = caps.Formats[0];
            for (uint i = 0; i < caps.FormatCount; i++)
            {
                var f = caps.Formats[i];
                if (f == TextureFormat.Bgra8Unorm || f == TextureFormat.Rgba8Unorm) { chosen = f; break; }
            }
            SurfaceFormat = chosen;
            Logger.Info($"wgpu surface format: {SurfaceFormat}");
        }

        /// <summary>(Re)configure the swapchain to the given framebuffer size + present mode. Fifo is always
        /// supported; Mailbox/Immediate fall back to Fifo if the backend lacks them. Call on startup and resize.</summary>
        public void ConfigureSurface(int width, int height, PresentMode presentMode)
        {
            if (width <= 0 || height <= 0) return;
            SurfaceWidth = width;
            SurfaceHeight = height;

            var caps = new SurfaceCapabilities();
            Api.SurfaceGetCapabilities(Surface, Adapter, ref caps);
            var supported = false;
            for (nuint i = 0; i < caps.PresentModeCount; i++)
                if (caps.PresentModes[i] == presentMode) { supported = true; break; }
            if (!supported) presentMode = PresentMode.Fifo;

            var config = new SurfaceConfiguration
            {
                Device = Device,
                Format = SurfaceFormat,
                Usage = TextureUsage.RenderAttachment,
                PresentMode = presentMode,
                Width = (uint)width,
                Height = (uint)height,
                AlphaMode = CompositeAlphaMode.Auto,
            };
            Api.SurfaceConfigure(Surface, in config);
        }

        private void OnUncapturedError(ErrorType type, byte* message, void* _)
        {
            Logger.Error($"wgpu error [{type}]: {SilkMarshal.PtrToString((nint)message)}");
        }

        public void Dispose()
        {
            if (Queue != null) Api.QueueRelease(Queue);
            if (Device != null) Api.DeviceRelease(Device);
            if (Adapter != null) Api.AdapterRelease(Adapter);
            if (Surface != null) Api.SurfaceRelease(Surface);
            if (Instance != null) Api.InstanceRelease(Instance);
            Api.Dispose();
        }
    }
}

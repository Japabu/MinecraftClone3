using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Graphics.Rhi
{
    /// <summary>
    /// The wgpu-native optional features the modern renderer paths gate on, detected against the adapter at
    /// startup. Each is a graceful capability switch: when a feature is absent the renderer falls back
    /// (an N-draw loop for missing multi-draw, dynamic-offset UBOs for missing push constants).
    /// </summary>
    public sealed unsafe class GpuFeatures
    {
        /// <summary><c>MultiDrawIndirectCount</c>: GPU-driven culling writes the draw list + count, one call draws it.</summary>
        public bool MultiDrawIndirectCount { get; private init; }

        /// <summary><c>PushConstants</c>: cheap per-draw constants for GUI sprites / outlines / block-break.</summary>
        public bool PushConstants { get; private init; }

        /// <summary>Timestamp queries for per-pass GPU timing (the F3 overlay's gpuMs).</summary>
        public bool TimestampQuery { get; private init; }

        private GpuFeatures() { }

        public static GpuFeatures Detect(WebGPU api, Adapter* adapter)
        {
            var f = new GpuFeatures
            {
                MultiDrawIndirectCount = Has(api, adapter, (FeatureName)NativeFeature.MultiDrawIndirectCount),
                PushConstants = Has(api, adapter, (FeatureName)NativeFeature.PushConstants),
                TimestampQuery = api.AdapterHasFeature(adapter, FeatureName.TimestampQuery),
            };
            Logger.Info($"wgpu features: MultiDrawIndirectCount={f.MultiDrawIndirectCount} " +
                        $"PushConstants={f.PushConstants} TimestampQuery={f.TimestampQuery}");
            return f;
        }

        private static bool Has(WebGPU api, Adapter* adapter, FeatureName feature)
            => api.AdapterHasFeature(adapter, feature);

        /// <summary>
        /// Allocate the native <see cref="FeatureName"/> array to request on the device, returning its count.
        /// Caller frees the returned pointer with <see cref="SilkMarshal.Free"/>.
        /// </summary>
        public FeatureName* BuildEnabledList(out uint count)
        {
            var list = new System.Collections.Generic.List<FeatureName>();
            if (MultiDrawIndirectCount) list.Add((FeatureName)NativeFeature.MultiDrawIndirectCount);
            if (PushConstants) list.Add((FeatureName)NativeFeature.PushConstants);
            if (TimestampQuery) list.Add(FeatureName.TimestampQuery);

            count = (uint)list.Count;
            if (count == 0) return null;
            var ptr = (FeatureName*)SilkMarshal.Allocate(list.Count * sizeof(FeatureName));
            for (var i = 0; i < list.Count; i++) ptr[i] = list[i];
            return ptr;
        }

        /// <summary>Query wgpu-native's <c>maxPushConstantSize</c> via the chained limits-extras struct.</summary>
        public uint QueryMaxPushConstantSize(WebGPU api, Adapter* adapter)
        {
            var extras = new SupportedLimitsExtras
            {
                Chain = new ChainedStructOut { SType = (SType)NativeSType.STypeSupportedLimitsExtras },
            };
            var supported = new SupportedLimits { NextInChain = (ChainedStructOut*)&extras };
            api.AdapterGetLimits(adapter, ref supported);
            return extras.Limits.MaxPushConstantSize;
        }
    }
}

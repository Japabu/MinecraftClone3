using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics.Rhi;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Silk.NET.Maths;
using Silk.NET.WebGPU;

namespace MinecraftClone3API.Graphics
{
    public static class WorldRenderer
    {
        // User render distance (GuiGraphicsOptions slider → GraphicsSettings.RenderDistanceChunks). Computed
        // live each frame so the slider takes effect next frame with no reload; in SP the server's view/load
        // radius and the client cache distance are driven from the same setting (see StateWorld), in MP it
        // only shrinks/grows what the client DRAWS (it can't pull more than the server streams).
        public static float RenderDistance => GraphicsSettings.RenderDistanceChunks * Chunk.Size;
        public static float RenderDistanceSq => RenderDistance * RenderDistance;

        //Changeable in settings
        public const float SortDistance = 128;
        public const float SortDistanceSq = SortDistance * SortDistance;

        // Reused across frames so the per-frame transparent scan allocates nothing steady-state. Opaque/LOD/
        // shadow are GPU-culled now (the cull compute builds the indirect draw list); only the transparent
        // chunks still need a CPU back-to-front sort, so these are the only per-frame visible-set lists kept.
        private static readonly List<ChunkRenderData> _transparentSortedChunks = new List<ChunkRenderData>(1024);
        private static readonly List<ChunkRenderData> _transparentChunks = new List<ChunkRenderData>(1024);

        // The block radius the Phase-2 distant-horizon LOD ring extends to (0 ⇒ LOD dormant), captured per
        // frame from the world. The cull compute frustum/distance-tests the LOD regions on the GPU.
        private static float _lodRenderDistance;

        // Width (blocks) of the dithered cross-fade band just inside the render distance, where full-detail
        // chunks dissolve into the Phase-2 horizon LOD instead of popping. The smoothness↔overdraw knob.
        private const float FadeBandWidth = 32f;

        // Read by the cached transparent-sort comparator so the delegate is allocated once instead of
        // capturing a fresh closure (over camera position) every frame.
        private static Vector3 _sortCameraPos;
        private static readonly Comparison<ChunkRenderData> _transparentSort = (chunk1, chunk2)
            => (int) ((_sortCameraPos - chunk2.Middle).LengthSquared * 1000 -
                      (_sortCameraPos - chunk1.Middle).LengthSquared * 1000);

        // Refilled in place each frame instead of allocating a fresh Plane[6] + 6 planes per call.
        private static readonly Frustum _viewFrustum = new Frustum();

        // Sun shadow map. A single orthographic depth map from the sun's point of view, fit each frame to the
        // analytic bounding sphere of the [ShadowNear, ShadowDistance] view-frustum slice (radius depends only
        // on the slice + FOV, so it is constant as the camera rotates -> no size shimmer). Geometry past
        // ShadowDistance casts no shadow (faded out smoothly). ShadowDistance is the coverage knob and
        // ShadowMapSize the resolution knob; the map is deliberately low-res for a soft, blurry look (the PCF
        // penumbra grows with the coarse world-per-texel) -- see CLAUDE.md.
        public const float ShadowNear = 0.5f;

        // Shadow coverage distance + map resolution, driven by the Shadow Quality preset (GraphicsSettings.
        // ShadowQuality): Low 96/512, Medium 160/1024 (default), High 256/2048. Computed live so the preset
        // applies next frame; the shadow map texture is recreated by EnsureTargets when the size changes.
        public static float ShadowDistance => GraphicsSettings.ShadowQuality switch
        {
            ShadowQuality.Low => 96f,
            ShadowQuality.High => 256f,
            _ => 160f
        };

        public static int ShadowMapSize => GraphicsSettings.ShadowQuality switch
        {
            ShadowQuality.Low => 512,
            ShadowQuality.High => 2048,
            _ => 1024
        };

        // Pull the light eye this far past the bounding sphere toward the sun so casters just outside the slice
        // (e.g. a tall block) still shadow into it.
        private const float ShadowCasterExtent = 64f;

        // Shadow look knobs (uploaded as shader uniforms each frame, so tunable here with no shader recompile).
        // ShadowStrength (0..1): how dark a fully-shadowed surface goes -- 1 = can reach black, lower lifts the
        // floor so sun shadows aren't crushed (only the direct-sun term, not ambient/caves). ShadowSoftness:
        // PCF penumbra radius in shadow texels -- larger = softer, at no extra tap cost.
        public static float ShadowStrength = 0.65f;
        public static float ShadowSoftness = 8f;

        // Sun-altitude band (toSun.Y) over which the directional sun term -- and with it the shadows -- fade
        // in/out. Below ShadowFadeLow the sun is treated as down (shadow pass skipped, term zero). Fading the
        // whole sun term (not just toggling shadows) makes lit areas dim down to meet the shadowed ones at
        // dusk, so there is no brightening pop when the shadow pass cuts off. See CLAUDE.md.
        private const float ShadowFadeLow = 0.05f;
        private const float ShadowFadeHigh = 0.15f;

        private static readonly Frustum _shadowFrustum = new Frustum();
        private static Matrix4 _lightViewProj;
        // World units per shadow texel (the shader scales its normal-offset bias by this).
        private static float _shadowTexelWorld;

        // Set by the per-frame scan: true iff a visible chunk within ShadowDistance is sky-exposed. Gates the
        // sun shadow passes — deep in a cave no visible chunk is sky-lit, so the shadow depth pass (a fixed
        // per-frame cost, see CLAUDE.md) is skipped; look toward the surface and it runs again.
        private static bool _anyShadowReceiver;

        // Distance past which a sky-exposed chunk no longer forces the shadow passes (matches the shadow-resolve
        // shader, which early-outs shadow sampling past the shadow distance).
        public static float ShadowDistanceSq => ShadowDistance * ShadowDistance;

        // Day/night clock. The sun colour drives the sky-light term in the composition shader, so the whole
        // world brightens/dims over the cycle with no remesh. The time is server-authoritative
        // (WorldClient.WorldTimeSeconds, synced from WorldTimePacket and advanced locally), so multiplayer
        // clients share one time of day. Set once at the top of RenderWorld. Starts at noon (the 0.25 phase).
        private const float DayLengthSeconds = 1200f;
        private static double _dayTimeSeconds;

        // Per-dimension visuals, set per-frame from the client world (content provides the values via the
        // dimension; the engine stays generic). A sunless dimension has no day-night: the sky functions return a
        // flat _fogColor, the directional sun term is forced off (no shadow pass), and _ambientFloor (uniform
        // uAmbientFloor) is a minimum light so unlit ground stays visible. The Overworld keeps _hasSky and a zero
        // ambient floor, so nothing changes there.
        private static bool _hasSky = true;
        private static Vector3 _fogColor;
        private static Vector3 _ambientFloor;

        // Underwater overlay: true when the camera's eye sits inside a liquid block, plus the murk colour the
        // scene fogs into (a deep water blue dimmed by the current daylight). Set per-frame in RenderWorld and
        // uploaded to the composition shader, which fogs everything (and the sky) into it over a short distance.
        private static bool _underwater;
        private static Vector3 _underwaterColor;

        // Sun/moon textures from the resource pack (sun.png and the full-moon celestial texture), sampled by
        // the composition shader to draw the sky billboards.
        public static Texture SunTexture;
        public static Texture MoonTexture;

        // Sun/moon billboard sizes (tan of the half-angle). The Minecraft sun is famously large; these are an
        // art-direction choice, tune to taste.
        private const float SunSize = 0.18f;
        private const float MoonSize = 0.12f;

        // Normalized clock position (0.25 = noon at startup) and the sun altitude sine derived from it; shared
        // by every time-of-day function so the sun colour, sky gradient, and sun direction stay in lockstep.
        private static float DayTime() => 0.25f + (float) (_dayTimeSeconds / DayLengthSeconds);
        private static float SunHeight() => MathF.Sin(DayTime() * MathF.PI * 2f);

        /// <summary>Day factor (0 night .. 1 full day) used to cross-fade every time-of-day colour: 0 once the
        /// sun is well below the horizon, 1 once it is well above.</summary>
        private static float DayFactor() => Math.Clamp((SunHeight() + 0.2f) / 0.4f, 0f, 1f);

        /// <summary>Loads the sun and moon textures from the active resource pack. Must run after the resource
        /// packs are indexed (see GuiResourceLoading).</summary>
        public static void LoadSkyTextures()
        {
            SunTexture = LoadPackTexture("minecraft/textures/environment/celestial/sun.png");
            MoonTexture = LoadPackTexture("minecraft/textures/environment/celestial/moon/full_moon.png");
        }

        private static Texture LoadPackTexture(string key)
        {
            if (!ResourceReader.Exists(key))
            {
                Logger.Error($"Sky texture \"{key}\" not found.");
                return null;
            }

            try { return GlResources.ReadTexture(key); }
            catch (Exception e)
            {
                Logger.Error($"Failed to load sky texture \"{key}\".");
                Logger.Exception(e);
                return null;
            }
        }

        /// <summary>When set (the benchmark sets it), the day clock is pinned to this many seconds instead of
        /// the server-authoritative time, so every benchmark run sees identical sun/shadow conditions. Null in
        /// normal play (then <see cref="_dayTimeSeconds"/> tracks <c>WorldClient.WorldTimeSeconds</c>).</summary>
        public static double? FixedTimeOfDay;

        /// <summary>Sun colour/intensity for the current time of day: bright warm white at noon, orange at
        /// the horizon (sunrise/sunset), a dim blue at night. Multiplied by the baked sky factor in the
        /// composition shader.</summary>
        private static Vector3 SunColor()
        {
            if (!_hasSky) return Vector3.Zero;
            var sunHeight = SunHeight();
            var day = Math.Clamp((sunHeight + 0.2f) / 0.4f, 0f, 1f);
            var highness = Math.Clamp(sunHeight, 0f, 1f);

            var horizon = new Vector3(1.0f, 0.5f, 0.25f);
            var noon = new Vector3(1.0f, 0.98f, 0.92f);
            var night = new Vector3(0.04f, 0.05f, 0.09f);

            var dayColor = Vector3D.Lerp(horizon, noon, highness);
            return Vector3D.Lerp(night, dayColor, day);
        }

        /// <summary>Ambient sky light for the current time of day: the soft blue fill that lights sky-exposed
        /// surfaces the sun can't reach (daytime shadows), and the dim cool moonlight that replaces the sun at
        /// night. Scaled by the baked sky factor in the composition shader, so it never reaches sky-occluded
        /// caves — those stay dark unless a block light reaches them. Tune for darker/brighter ambient.</summary>
        private static Vector3 SkyAmbient()
        {
            if (!_hasSky) return _ambientFloor;
            var day = DayFactor();
            var moon = new Vector3(0.05f, 0.06f, 0.11f);
            var sky = new Vector3(0.12f, 0.15f, 0.20f);
            return Vector3D.Lerp(moon, sky, day);
        }

        /// <summary>Zenith colour of the background sky gradient: deep blue overhead by day, near-black at
        /// night. The composition shader blends it toward <see cref="SkyHorizonColor"/> down to the horizon.</summary>
        private static Vector3 SkyZenithColor()
        {
            if (!_hasSky) return _fogColor;
            var day = DayFactor();
            var night = new Vector3(0.00f, 0.01f, 0.03f);
            var sky = new Vector3(0.28f, 0.50f, 0.92f);
            return Vector3D.Lerp(night, sky, day);
        }

        /// <summary>Horizon haze colour: a light hazy blue by day, dark at night. Doubles as the distance-fog
        /// colour in the composition shader so terrain melts into the horizon at the render-distance edge.</summary>
        private static Vector3 SkyHorizonColor()
        {
            if (!_hasSky) return _fogColor;
            var day = DayFactor();
            var night = new Vector3(0.01f, 0.02f, 0.05f);
            var sky = new Vector3(0.66f, 0.78f, 0.93f);
            return Vector3D.Lerp(night, sky, day);
        }

        /// <summary>Colour below the horizon (the void): a dim grey-blue by day, near-black at night.</summary>
        private static Vector3 SkyVoidColor()
        {
            if (!_hasSky) return _fogColor;
            var day = DayFactor();
            var night = new Vector3(0.00f, 0.00f, 0.01f);
            var voidColor = new Vector3(0.18f, 0.22f, 0.28f);
            return Vector3D.Lerp(night, voidColor, day);
        }

        /// <summary>Sunrise/sunset glow colour, strongest when the sun is near the horizon and fading to black
        /// by noon and deep night. The composition shader paints it as an orange band near the horizon in the
        /// sun's azimuth.</summary>
        private static Vector3 SunsetColor()
        {
            if (!_hasSky) return Vector3.Zero;
            var band = Math.Clamp(1f - MathF.Abs(SunHeight()) / 0.30f, 0f, 1f);
            return new Vector3(0.85f, 0.42f, 0.16f) * band;
        }

        /// <summary>Star brightness (0 by day, up to 1 at night), faded by the same day factor as everything
        /// else so the stars come out smoothly as the sky darkens.</summary>
        private static float StarBrightness() => _hasSky ? Math.Clamp(1f - DayFactor(), 0f, 1f) : 0f;

        /// <summary>Unit vector pointing from the world toward the sun, animated by the same day/night
        /// clock as <see cref="SunColor"/> so the brightest sun aligns with the highest sun. Drives the
        /// shadow map's orthographic sun camera; the normalized Y is the sun altitude (≤0 ⇒ below the
        /// horizon, shadow pass skipped). The small Z tilt keeps shadows off the world axes.</summary>
        private static Vector3 SunDirection()
        {
            var angle = DayTime() * MathF.PI * 2f;
            return Vector3D.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));
        }

        /// <summary>Directional sun-term strength (0..1) for the current sun altitude: a smoothstep that ramps
        /// the sun/shadow contribution down to zero as the sun reaches the horizon, so dusk fades smoothly to
        /// ambient. 0 at/below <see cref="ShadowFadeLow"/> (sun treated as down → the shadow pass is skipped).</summary>
        private static float SunFade(float sunY)
        {
            var t = Math.Clamp((sunY - ShadowFadeLow) / (ShadowFadeHigh - ShadowFadeLow), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        // ----- GPU pipeline state (lazy-built once the device + resources exist) -------------------------------

        private static bool _loaded;
        private const string ShaderDir = "System/Shaders/";

        private static ChunkCuller _opaqueCull;
        private static ChunkCuller _shadowCull;
        private static ChunkCuller _lodCull;

        /// <summary>GeoParams UBO (16 B): the LOD cross-fade band + the cutout alpha-test flag. Three flavours
        /// (opaque / LOD horizon / transparent), written each frame, one bind group each.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct GeoParams
        {
            public float FadeStart;
            public float FadeEnd;
            public uint FadeMode;
            public uint Cutoff;
        }

        /// <summary>ShadowResolveParams UBO (224 B) — byte layout mirrors ShadowResolve.wgsl's header exactly.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ShadowResolveParams
        {
            public Mat4 ViewProjectionInv;
            public Mat4 View;
            public Mat4 LightViewProj;
            public float ShadowTexel;
            public float ShadowDistance;
            public float SunFade;
            public float ShadowsEnabled;
            public float ShadowSoftness;
            public float ShadowMapTexel;
            public float Pad0;
            public float Pad1;
        }

        /// <summary>CompositionParams UBO (320 B) — byte layout mirrors Composition.wgsl's header exactly. Each
        /// vec3 field is immediately followed by the scalar that fills its 16-byte tail padding.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct CompositionParams
        {
            public Mat4 ViewProjectionInv;
            public Mat4 View;
            public Vector3 SunColor; public float ShadowDistance;
            public Vector3 SkyAmbient; public float SunFade;
            public Vector3 MinLight; public float ShadowStrength;
            public Vector3 AmbientFloor; public float ShadowsEnabled;
            public Vector3 CameraPos; public float DebugShadow;
            public Vector3 SunDirection; public float Time;
            public Vector3 MoonColor; public float MoonFade;
            public Vector3 SkyColor; public float StarBrightness;
            public Vector3 HorizonColor; public float SunSize;
            public Vector3 VoidColor; public float MoonSize;
            public Vector3 SunsetColor; public float SkyDistance;
            public float FogStart; public float FogEnd;
            public float Pad0; public float Pad1;
            public Vector3 UnderwaterColor; public float Underwater;
        }

        private static GpuShaderModule _geometryModule;
        private static GpuBindGroupLayout _geoParamsLayout;
        private static GpuPipelineLayout _geometryPipeLayout;
        private static GpuRenderPipeline _geometryPipeline;
        private static GpuRenderPipeline _transparentPipeline;

        private static GpuBuffer _geoOpaqueUbo, _geoLodUbo, _geoTransparentUbo;
        private static GpuBindGroup _geoOpaqueBind, _geoLodBind, _geoTransparentBind;

        private static GpuShaderModule _shadowModule;
        private static GpuPipelineLayout _shadowPipeLayout;
        private static GpuRenderPipeline _shadowPipeline;
        private static GpuBuffer _shadowFrameUbo;
        private static GpuBindGroup _shadowFrameBind;

        private static GpuShaderModule _shadowResolveModule;
        private static GpuBindGroupLayout _shadowResolveParamsLayout;
        private static GpuBindGroupLayout _shadowResolveTexLayout;
        private static GpuPipelineLayout _shadowResolvePipeLayout;
        private static GpuRenderPipeline _shadowResolvePipeline;
        private static GpuBuffer _shadowResolveUbo;
        private static GpuBindGroup _shadowResolveParamsBind;
        private static GpuBindGroup _shadowResolveTexBind;

        private static GpuShaderModule _compositionModule;
        private static GpuBindGroupLayout _compositionParamsLayout;
        private static GpuBindGroupLayout _compositionTexLayout;
        private static GpuPipelineLayout _compositionPipeLayout;
        private static GpuRenderPipeline _compositionPipeline;
        private static GpuBuffer _compositionUbo;
        private static GpuBindGroup _compositionParamsBind;
        private static GpuBindGroup _compositionTexBind;

        private static GpuBindGroup _atlasBind;
        private static GpuTexture _atlasArray0Cache;

        private static GpuTexture _shadowMap;
        private static int _shadowMapSize;
        private static GpuTexture _shadowResolve;
        private static int _resolveWidth, _resolveHeight;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            ChunkCuller.Load(ResourceReader.ReadString);
            _opaqueCull = new ChunkCuller("opaque");
            _shadowCull = new ChunkCuller("shadow");
            _lodCull = new ChunkCuller("lod");

            BuildGeometryPipelines();
            BuildShadowPipeline();
            BuildShadowResolvePipeline();
            BuildCompositionPipeline();
        }

        private static void BuildGeometryPipelines()
        {
            _geometryModule = new GpuShaderModule(ResourceReader.ReadString(ShaderDir + "WorldGeometry.wgsl"), "worldGeometry");
            _geoParamsLayout = new GpuBindGroupLayout(new[]
            {
                GpuBindGroupLayout.Buffer(0, ShaderStage.Vertex | ShaderStage.Fragment, BufferBindingType.Uniform),
            }, "geoParams");

            _geometryPipeLayout = new GpuPipelineLayout(new[]
            {
                GpuPipelineLayout.Ptr(GpuLayouts.Frame),
                GpuPipelineLayout.Ptr(_geoParamsLayout),
                GpuPipelineLayout.Ptr(GpuLayouts.BlockAtlas),
            }, label: "worldGeometry");

            var colorFormats = GBufferTargets.ColorFormats;
            Span<ColorTargetDesc> opaqueTargets = stackalloc ColorTargetDesc[3]
            {
                new ColorTargetDesc(colorFormats[0]),
                new ColorTargetDesc(colorFormats[1]),
                new ColorTargetDesc(colorFormats[2]),
            };
            // GreaterEqual (the reverse-Z equivalent of GL's Lequal): a coplanar face at exactly the same depth
            // still draws, so a block model's overlay layer (e.g. the grass-side tinted overlay over its base)
            // isn't depth-rejected — a strict Greater would drop it and lose the tint.
            var geoDepth = new DepthDesc(GBufferTargets.DepthFormat, true, CompareFunction.GreaterEqual);
            // NOTE: if all terrain renders inside-out/invisible, flip FrontFace or CullMode — winding-convention check
            _geometryPipeline = new GpuRenderPipeline(_geometryPipeLayout, _geometryModule, "vs_main", "fs_main",
                ChunkMeshArena.GeometryVertexLayout, opaqueTargets, geoDepth,
                cullMode: CullMode.Back, frontFace: FrontFace.Ccw, label: "worldGeometry");

            // Transparent: same shader/layout, but the diffuse target alpha-blends while normal/light write
            // cleanly (so the water flag in normal.w and the surface's own light survive composition).
            Span<ColorTargetDesc> transparentTargets = stackalloc ColorTargetDesc[3]
            {
                new ColorTargetDesc(colorFormats[0], GpuRenderPipeline.AlphaBlend),
                new ColorTargetDesc(colorFormats[1]),
                new ColorTargetDesc(colorFormats[2]),
            };
            _transparentPipeline = new GpuRenderPipeline(_geometryPipeLayout, _geometryModule, "vs_main", "fs_main",
                ChunkMeshArena.GeometryVertexLayout, transparentTargets, geoDepth,
                cullMode: CullMode.Back, frontFace: FrontFace.Ccw, label: "worldTransparent");

            var geoBytes = (ulong) Marshal.SizeOf<GeoParams>();
            _geoOpaqueUbo = new GpuBuffer(geoBytes, BufferUsage.Uniform | BufferUsage.CopyDst, "geoParams.opaque");
            _geoLodUbo = new GpuBuffer(geoBytes, BufferUsage.Uniform | BufferUsage.CopyDst, "geoParams.lod");
            _geoTransparentUbo = new GpuBuffer(geoBytes, BufferUsage.Uniform | BufferUsage.CopyDst, "geoParams.transparent");

            _geoOpaqueBind = new GpuBindGroup(_geoParamsLayout, new[] { GpuBindGroup.Buffer(0, _geoOpaqueUbo) }, "geoParams.opaque");
            _geoLodBind = new GpuBindGroup(_geoParamsLayout, new[] { GpuBindGroup.Buffer(0, _geoLodUbo) }, "geoParams.lod");
            _geoTransparentBind = new GpuBindGroup(_geoParamsLayout, new[] { GpuBindGroup.Buffer(0, _geoTransparentUbo) }, "geoParams.transparent");
        }

        private static void BuildShadowPipeline()
        {
            _shadowModule = new GpuShaderModule(ResourceReader.ReadString(ShaderDir + "ShadowDepth.wgsl"), "shadowDepth");
            // The ShadowFrame WGSL struct is a single mat4x4 at group(0) binding(0); the shared Frame BGL shape
            // (one vertex/fragment-visible uniform) matches it, so reuse it for the bind group.
            _shadowPipeLayout = new GpuPipelineLayout(new[] { GpuPipelineLayout.Ptr(GpuLayouts.Frame) }, label: "shadowDepth");
            // depthBias(2)/slopeScale(4) for the shadow pass. Under reverse-Z the bias sign may
            // need flipping (logged in docs/known-issues.md). Cull None: the mesher emits single-sided faces.
            var shadowDepth = new DepthDesc(GBufferTargets.DepthFormat, true, CompareFunction.Greater,
                depthBias: 2, depthBiasSlopeScale: 4f);
            _shadowPipeline = new GpuRenderPipeline(_shadowPipeLayout, _shadowModule, "vs_main", null,
                ChunkMeshArena.ShadowVertexLayout, ReadOnlySpan<ColorTargetDesc>.Empty, shadowDepth,
                cullMode: CullMode.None, label: "shadowDepth");

            _shadowFrameUbo = new GpuBuffer((ulong) Marshal.SizeOf<Mat4>(),
                BufferUsage.Uniform | BufferUsage.CopyDst, "shadowFrame");
            _shadowFrameBind = new GpuBindGroup(GpuLayouts.Frame, new[] { GpuBindGroup.Buffer(0, _shadowFrameUbo) }, "shadowFrame");
        }

        private static void BuildShadowResolvePipeline()
        {
            _shadowResolveModule = new GpuShaderModule(ResourceReader.ReadString(ShaderDir + "ShadowResolve.wgsl"), "shadowResolve");
            _shadowResolveParamsLayout = new GpuBindGroupLayout(new[]
            {
                GpuBindGroupLayout.Buffer(0, ShaderStage.Fragment, BufferBindingType.Uniform),
            }, "shadowResolveParams");
            _shadowResolveTexLayout = new GpuBindGroupLayout(new[]
            {
                GpuBindGroupLayout.Texture(0, ShaderStage.Fragment, TextureSampleType.Float),
                GpuBindGroupLayout.Texture(1, ShaderStage.Fragment, TextureSampleType.Depth),
                GpuBindGroupLayout.Texture(2, ShaderStage.Fragment, TextureSampleType.Float),
                GpuBindGroupLayout.Texture(3, ShaderStage.Fragment, TextureSampleType.Depth),
                GpuBindGroupLayout.Sampler(4, ShaderStage.Fragment, SamplerBindingType.Filtering),
                GpuBindGroupLayout.Sampler(5, ShaderStage.Fragment, SamplerBindingType.Comparison),
            }, "shadowResolveTex");
            _shadowResolvePipeLayout = new GpuPipelineLayout(new[]
            {
                GpuPipelineLayout.Ptr(_shadowResolveParamsLayout),
                GpuPipelineLayout.Ptr(_shadowResolveTexLayout),
            }, label: "shadowResolve");
            _shadowResolvePipeline = new GpuRenderPipeline(_shadowResolvePipeLayout, _shadowResolveModule, "vs_main", "fs_main",
                ReadOnlySpan<VertexBufferDesc>.Empty,
                stackalloc[] { new ColorTargetDesc(TextureFormat.Rgba8Unorm) }, depth: null, label: "shadowResolve");

            _shadowResolveUbo = new GpuBuffer((ulong) Marshal.SizeOf<ShadowResolveParams>(),
                BufferUsage.Uniform | BufferUsage.CopyDst, "shadowResolveParams");
            _shadowResolveParamsBind = new GpuBindGroup(_shadowResolveParamsLayout,
                new[] { GpuBindGroup.Buffer(0, _shadowResolveUbo) }, "shadowResolveParams");
        }

        private static void BuildCompositionPipeline()
        {
            _compositionModule = new GpuShaderModule(ResourceReader.ReadString(ShaderDir + "Composition.wgsl"), "composition");
            _compositionParamsLayout = new GpuBindGroupLayout(new[]
            {
                GpuBindGroupLayout.Buffer(0, ShaderStage.Fragment, BufferBindingType.Uniform),
            }, "compositionParams");
            _compositionTexLayout = new GpuBindGroupLayout(new[]
            {
                GpuBindGroupLayout.Texture(0, ShaderStage.Fragment, TextureSampleType.Float),
                GpuBindGroupLayout.Texture(1, ShaderStage.Fragment, TextureSampleType.Float),
                GpuBindGroupLayout.Texture(2, ShaderStage.Fragment, TextureSampleType.Depth),
                GpuBindGroupLayout.Texture(3, ShaderStage.Fragment, TextureSampleType.Float),
                GpuBindGroupLayout.Texture(4, ShaderStage.Fragment, TextureSampleType.Float),
                GpuBindGroupLayout.Texture(5, ShaderStage.Fragment, TextureSampleType.Float),
                GpuBindGroupLayout.Texture(6, ShaderStage.Fragment, TextureSampleType.Float),
                GpuBindGroupLayout.Sampler(7, ShaderStage.Fragment, SamplerBindingType.Filtering),
                GpuBindGroupLayout.Sampler(8, ShaderStage.Fragment, SamplerBindingType.Filtering),
            }, "compositionTex");
            _compositionPipeLayout = new GpuPipelineLayout(new[]
            {
                GpuPipelineLayout.Ptr(_compositionParamsLayout),
                GpuPipelineLayout.Ptr(_compositionTexLayout),
            }, label: "composition");
            _compositionPipeline = new GpuRenderPipeline(_compositionPipeLayout, _compositionModule, "vs_main", "fs_main",
                ReadOnlySpan<VertexBufferDesc>.Empty,
                stackalloc[] { new ColorTargetDesc(Renderer.HdrFormat) }, depth: null, label: "composition");

            _compositionUbo = new GpuBuffer((ulong) Marshal.SizeOf<CompositionParams>(),
                BufferUsage.Uniform | BufferUsage.CopyDst, "compositionParams");
            _compositionParamsBind = new GpuBindGroup(_compositionParamsLayout,
                new[] { GpuBindGroup.Buffer(0, _compositionUbo) }, "compositionParams");
        }

        /// <summary>Builds (and rebuilds on resize / shadow-quality change) the shadow map + half-res resolve
        /// targets owned by the world renderer. The G-buffer + HDR scene targets are owned elsewhere
        /// (ClientResources / Renderer); only their views are referenced. Invalidates the texture bind groups
        /// (rebuilt lazily) when a target it references is recreated.</summary>
        private static void EnsureTargets()
        {
            if (_shadowMap == null || _shadowMapSize != ShadowMapSize)
            {
                _shadowMapSize = ShadowMapSize;
                _shadowMap?.Dispose();
                _shadowMap = new GpuTexture((uint) _shadowMapSize, (uint) _shadowMapSize, TextureFormat.Depth32float,
                    TextureUsage.RenderAttachment | TextureUsage.TextureBinding, label: "shadowMap");
                _shadowResolveTexBind = null;
            }

            var halfW = Math.Max(1, ClientResources.Width / 2);
            var halfH = Math.Max(1, ClientResources.Height / 2);
            if (_shadowResolve == null || _resolveWidth != halfW || _resolveHeight != halfH)
            {
                _resolveWidth = halfW;
                _resolveHeight = halfH;
                _shadowResolve?.Dispose();
                _shadowResolve = new GpuTexture((uint) halfW, (uint) halfH, TextureFormat.Rgba8Unorm,
                    TextureUsage.RenderAttachment | TextureUsage.TextureBinding, label: "shadowResolve");
                _compositionTexBind = null;
            }

            // The G-buffer textures are recreated on framebuffer resize (ClientResources), so the texture bind
            // groups that sample them must be rebuilt too. Width/Height changing implies a resolve-size change
            // above, which already nulls them; this is the defensive belt for any out-of-band G-buffer recreate.
            if (ClientResources.GBuffer.Width != _resolveWidth * 2 && _resolveWidth * 2 != ClientResources.Width)
            {
                _shadowResolveTexBind = null;
                _compositionTexBind = null;
            }
        }

        private static unsafe void EnsureAtlasBind()
        {
            var array0 = BlockTextureUploader.ArrayAt(0);
            if (_atlasBind != null && _atlasArray0Cache == array0) return;
            _atlasBind?.Dispose();
            _atlasBind = new GpuBindGroup(GpuLayouts.BlockAtlas, new[]
            {
                GpuBindGroup.Texture(0, BlockTextureUploader.ArrayAt(0).View),
                GpuBindGroup.Texture(1, BlockTextureUploader.ArrayAt(1).View),
                GpuBindGroup.Texture(2, BlockTextureUploader.ArrayAt(2).View),
                GpuBindGroup.Texture(3, BlockTextureUploader.ArrayAt(3).View),
                GpuBindGroup.Sampler(4, GpuSamplers.Block),
                GpuBindGroup.Sampler(5, GpuSamplers.BlockAniso),
            }, "blockAtlas");
            _atlasArray0Cache = array0;
        }

        public static unsafe void RenderWorld(WorldClient world, Matrix4 projection)
        {
            EnsureLoaded();
            EnsureTargets();
            EnsureAtlasBind();

            var camera = PlayerController.Camera;

            // Server-authoritative time of day; sampled once so every sun term this frame is consistent. The
            // benchmark pins it (FixedTimeOfDay) for reproducible sun/shadow conditions.
            _dayTimeSeconds = FixedTimeOfDay ?? world.WorldTimeSeconds;
            _hasSky = world.HasSky;
            _fogColor = world.FogColor;
            _ambientFloor = world.AmbientLight;

            var viewProjection = camera.View * projection;
            Matrix4X4.Invert(viewProjection, out var viewProjectionInv);
            _viewFrustum.Set(viewProjection);
            _lodRenderDistance = world.LodRenderDistance;

            Renderer.SetFrameUniform(camera.View, projection, camera.Position);

            var toSun = SunDirection();
            // The directional sun term -- and with it the shadows -- fade out together as the sun nears the
            // horizon, so dusk converges smoothly to ambient instead of the sun term popping when the shadow
            // pass cuts off. sunUp (term present) is exactly sunFade > 0.
            var sunFade = _hasSky ? SunFade(toSun.Y) : 0f;
            var sunUp = sunFade > 0f;

            // Eye-in-liquid underwater murk. Block P fills [P, P+1] (corner-origin), so floor(v) is the block
            // containing the camera — matching PlayerPhysics' liquid sampling. The murk colour dims with the
            // daylight (sun + ambient) so it's bright blue by day, near-black at night or in a flooded cave.
            var cam = camera.Position;
            _underwater = world.GetBlock((int) MathF.Floor(cam.X), (int) MathF.Floor(cam.Y),
                (int) MathF.Floor(cam.Z)).IsLiquid;
            var litTint = _hasSky ? SkyAmbient() + SunColor() * sunFade : _ambientFloor;
            var bright = Math.Clamp(MathF.Max(litTint.X, MathF.Max(litTint.Y, litTint.Z)), 0.12f, 1f);
            _underwaterColor = new Vector3(0.05f, 0.17f, 0.40f) * bright;

            // Slim CPU scan: gather the visible transparent chunks (back-to-front CPU sort — the GPU can't sort
            // alpha) and flag whether any visible chunk is sky-exposed (gates the shadow passes). Opaque + LOD +
            // shadow casters are GPU-culled, so no CPU opaque list is built.
            ScanTransparentAndShadow(world, camera, _viewFrustum);

            var shadowPass = sunUp && _anyShadowReceiver && GraphicsSettings.ShadowsEnabled;
            RenderDebug.ShadowPass = shadowPass;

            world.OpaqueArena.FlushMeta();
            world.LodArena.FlushMeta();

            var lodActive = _lodRenderDistance > RenderDistance && !world.ForceLodOff;

            var encoder = Renderer.Encoder;

            // --- Cull dispatches (compute) recorded BEFORE the geometry render pass: compute passes can't nest
            // inside a render pass. Each culler writes its own indirect draw list + count. ---
            if (shadowPass)
            {
                BuildLightMatrix(camera, projection, toSun);
                _shadowCull.Dispatch(encoder, world.OpaqueArena, _shadowFrustum, camera.Position,
                    maxDistance: 0f, chunkExtent: Chunk.Size);
            }
            _opaqueCull.Dispatch(encoder, world.OpaqueArena, _viewFrustum, camera.Position,
                RenderDistance, Chunk.Size);
            if (lodActive)
                _lodCull.Dispatch(encoder, world.LodArena, _viewFrustum, camera.Position,
                    _lodRenderDistance, 2f * 140f);

            // --- Shadow depth pass ---
            if (shadowPass) DrawShadowMap(world);

            // --- Geometry (G-buffer) pass: opaque (indirect multidraw), LOD horizon (indirect), transparent
            // (per-chunk back-to-front), then the overlays draw into the same pass. ---
            WriteGeoParams();
            var geomPass = ClientResources.GBuffer.BeginGeometryPass(encoder);
            geomPass.SetBindGroup(0, Renderer.FrameBindGroup);
            geomPass.SetBindGroup(2, _atlasBind);

            geomPass.SetPipeline(_geometryPipeline);
            geomPass.SetBindGroup(1, _geoOpaqueBind);
            world.OpaqueArena.BindGeometry(geomPass);
            _opaqueCull.Draw(geomPass);

            if (lodActive)
            {
                geomPass.SetBindGroup(1, _geoLodBind);
                world.LodArena.BindGeometry(geomPass);
                _lodCull.Draw(geomPass);
            }

            geomPass.SetPipeline(_transparentPipeline);
            geomPass.SetBindGroup(0, Renderer.FrameBindGroup);
            geomPass.SetBindGroup(1, _geoTransparentBind);
            geomPass.SetBindGroup(2, _atlasBind);
            foreach (var chunk in _transparentChunks)
                chunk.DrawTransparent(geomPass);

            EntityRenderer.Render(geomPass, world, camera);
            BlockEntityRenderer.Render(geomPass, world, camera);
            PlayerController.Render(geomPass, camera);
            ChunkBorderRenderer.Render(geomPass, camera);
            // Last: the first-person viewmodel compresses its depth band so it sits on top of the world.
            HeldItemRenderer.Render(geomPass, world, camera);

            geomPass.End();
            geomPass.Release();

            // --- Shadow resolve (half-res PCF) ---
            if (shadowPass) DrawShadowResolve(camera, viewProjectionInv, sunFade);

            // --- Composition into the HDR scene ---
            DrawComposition(camera, viewProjectionInv, sunFade, shadowPass, lodActive);

            Renderer.MarkSceneRendered();
        }

        /// <summary>Slim per-frame CPU scan: builds the back-to-front transparent draw list (the GPU can't sort
        /// alpha) and flags <see cref="_anyShadowReceiver"/> (true iff a visible sky-exposed chunk is within
        /// ShadowDistance). Opaque/LOD/shadow visibility is decided on the GPU by the cull compute.</summary>
        private static void ScanTransparentAndShadow(WorldClient world, Camera camera, Frustum viewFrustum)
        {
            _transparentSortedChunks.Clear();
            _transparentChunks.Clear();
            _anyShadowReceiver = false;

            var renderDistanceSq = RenderDistanceSq;
            var shadowDistanceSq = ShadowDistanceSq;

            var renderList = world.RenderList;
            for (var i = 0; i < renderList.Count; i++)
            {
                var renderData = renderList[i];
                var chunkMiddle = renderData.Middle;
                var lengthSq = (camera.Position - chunkMiddle).LengthSquared;
                if (lengthSq > renderDistanceSq) continue;
                if (!viewFrustum.SpehereIntersection(chunkMiddle, Chunk.Radius)) continue;

                if (renderData.HasTransparency)
                {
                    if (lengthSq < SortDistanceSq)
                    {
                        renderData.SortTransparentFaces();
                        _transparentSortedChunks.Add(renderData);
                    }
                    else _transparentChunks.Add(renderData);
                }

                if (renderData.SkyExposed && lengthSq <= shadowDistanceSq) _anyShadowReceiver = true;
            }

            // Sort the near transparent chunks back-to-front, then append them after the far ones so the closer
            // surfaces overdraw correctly.
            _sortCameraPos = camera.Position;
            _transparentSortedChunks.Sort(_transparentSort);
            _transparentChunks.AddRange(_transparentSortedChunks);
        }

        /// <summary>Computes the orthographic sun view-projection fit to the bounding sphere of the
        /// [ShadowNear, ShadowDistance] view-frustum slice (radius constant as the camera rotates → stable size,
        /// no shimmer) and the matching shadow frustum the GPU shadow cull tests against. Deliberately NOT
        /// texel-snapped — see CLAUDE.md.</summary>
        private static void BuildLightMatrix(Camera camera, Matrix4 projection, Vector3 toSun)
        {
            // Frustum half-angle tangents from the projection: Silk Matrix4X4 is 1-indexed, so Row1.X = 1/tan(fovX/2)
            // and Row2.Y = 1/tan(fovY/2). k2 turns a slice depth z into its corner radius z*sqrt(k2).
            var tanHalfH = 1f / projection.Row1.X;
            var tanHalfV = 1f / projection.Row2.Y;
            var k2 = tanHalfH * tanHalfH + tanHalfV * tanHalfV;

            // LookAt up vector; the UnitZ fallback only triggers if the sun is nearly straight up (never,
            // given SunDirection's permanent Z tilt).
            var up = MathF.Abs(toSun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

            // Analytic bounding sphere of the whole [ShadowNear, ShadowDistance] frustum slice. Centre rides
            // the camera forward axis (only the camera position, not its orientation, moves it); radius depends
            // solely on near/far + FOV, so it is constant across frames -> no rotation shimmer. When the sphere
            // through both corner rings would sit past the far plane, the far ring's circumscribed sphere is tighter.
            var n = ShadowNear;
            var f = ShadowDistance;
            var zc = (n + f) * (k2 + 1f) * 0.5f;
            float radius;
            if (zc >= f) { zc = f; radius = f * MathF.Sqrt(k2); }
            else radius = MathF.Sqrt(n * n * k2 + (n - zc) * (n - zc));

            var center = camera.Position + camera.Forward * zc;
            _shadowTexelWorld = 2f * radius / _shadowMapSize;

            var distBack = radius + ShadowCasterExtent;
            var lightEye = center + toSun * distBack;
            var lightView = Matrix4X4.CreateLookAt(lightEye, center, up);
            var lightProj = Projection.ReverseZOrtho(-radius, radius, -radius, radius, 0f, distBack + radius);
            _lightViewProj = lightView * lightProj;
            _shadowFrustum.Set(_lightViewProj);
        }

        /// <summary>Renders opaque chunk depth from the sun's orthographic point of view into the shadow map.
        /// One GPU-culled indirect multidraw over the opaque arena (position-only vertex stream); the
        /// shadow-resolve pass samples it to attenuate the sky/sun term.</summary>
        private static unsafe void DrawShadowMap(WorldClient world)
        {
            var encoder = Renderer.Encoder;
            _shadowFrameUbo.QueueWriteStruct(MatrixConvert.ToGpu(_lightViewProj));

            var depth = new DepthAttachment(_shadowMap.View, LoadOp.Clear, 0f);
            var pass = RenderPassBuilder.Begin(encoder, ReadOnlySpan<ColorAttachment>.Empty, depth);
            pass.SetPipeline(_shadowPipeline);
            pass.SetBindGroup(0, _shadowFrameBind);
            world.OpaqueArena.BindShadow(pass);
            _shadowCull.Draw(pass);
            pass.End();
            pass.Release();
        }

        /// <summary>Writes the three GeoParams UBOs for this frame. LOD cross-fade band: full-detail chunks
        /// dissolve into the horizon over [RD - FadeBandWidth, RD]; disabled (band pushed past the far plane)
        /// when the horizon is dormant so chunks never fade into nothing. Transparent never fades.</summary>
        private static void WriteGeoParams()
        {
            var fadeActive = _lodRenderDistance > RenderDistance;
            var fadeStart = fadeActive ? RenderDistance - FadeBandWidth : 1e9f;
            var fadeEnd = fadeActive ? RenderDistance : 1e9f + 1f;

            _geoOpaqueUbo.QueueWriteStruct(new GeoParams
                { FadeStart = fadeStart, FadeEnd = fadeEnd, FadeMode = 0, Cutoff = 1 });
            _geoLodUbo.QueueWriteStruct(new GeoParams
                { FadeStart = fadeStart, FadeEnd = fadeEnd, FadeMode = 1, Cutoff = 1 });
            _geoTransparentUbo.QueueWriteStruct(new GeoParams
                { FadeStart = 1e9f, FadeEnd = 1e9f + 1f, FadeMode = 0, Cutoff = 0 });
        }

        /// <summary>Resolves the sun shadow at half resolution: a fullscreen triangle runs the 12-tap PCF (moved
        /// out of composition) per half-res pixel, writing the shadow factor + normalized depth that composition
        /// depth-aware-upsamples. Reads the G-buffer normal/depth/light + the shadow depth map.</summary>
        private static unsafe void DrawShadowResolve(Camera camera, Matrix4 viewProjectionInv, float sunFade)
        {
            var encoder = Renderer.Encoder;
            _shadowResolveUbo.QueueWriteStruct(new ShadowResolveParams
            {
                ViewProjectionInv = MatrixConvert.ToGpu(viewProjectionInv),
                View = MatrixConvert.ToGpu(camera.View),
                LightViewProj = MatrixConvert.ToGpu(_lightViewProj),
                ShadowTexel = _shadowTexelWorld,
                ShadowDistance = ShadowDistance,
                SunFade = sunFade,
                ShadowsEnabled = GraphicsSettings.ShadowsEnabled ? 1f : 0f,
                ShadowSoftness = ShadowSoftness,
                ShadowMapTexel = 1f / _shadowMapSize,
            });

            if (_shadowResolveTexBind == null)
            {
                var gbuffer = ClientResources.GBuffer;
                _shadowResolveTexBind = new GpuBindGroup(_shadowResolveTexLayout, new[]
                {
                    GpuBindGroup.Texture(0, gbuffer.Normal.View),
                    GpuBindGroup.Texture(1, gbuffer.Depth.View),
                    GpuBindGroup.Texture(2, gbuffer.Light.View),
                    GpuBindGroup.Texture(3, _shadowMap.View),
                    GpuBindGroup.Sampler(4, GpuSamplers.Framebuffer),
                    GpuBindGroup.Sampler(5, GpuSamplers.ShadowCompare),
                }, "shadowResolveTex");
            }

            var color = ColorAttachment.ClearTo(_shadowResolve.View, 0, 0, 0, 1);
            var pass = RenderPassBuilder.Begin(encoder, stackalloc[] { color });
            pass.SetPipeline(_shadowResolvePipeline);
            pass.SetBindGroup(0, _shadowResolveParamsBind);
            pass.SetBindGroup(1, _shadowResolveTexBind);
            pass.Draw(3);
            pass.End();
            pass.Release();
        }

        /// <summary>Composes the G-buffer + baked light + resolved sun shadow into the HDR scene, draws the
        /// procedural sky for background pixels, shades water, and fogs into the horizon. All the day/night
        /// colours + matrices + sky/fog/water uniforms go into the 320-byte CompositionParams UBO.</summary>
        private static unsafe void DrawComposition(Camera camera, Matrix4 viewProjectionInv, float sunFade,
            bool shadowPass, bool lodActive)
        {
            var encoder = Renderer.Encoder;

            // Far-plane (sky) vs terrain split + distance fog. When the Phase-2 LOD horizon is active the drawn
            // geometry reaches LodRenderDistance, so the sky-distance threshold and the fog band move out to that
            // horizon (else the LOD ring would be painted as sky or fully fogged out). Dormant LOD ⇒ RenderDistance.
            var horizonDistance = lodActive ? _lodRenderDistance : RenderDistance;
            var toSun = SunDirection();

            _compositionUbo.QueueWriteStruct(new CompositionParams
            {
                ViewProjectionInv = MatrixConvert.ToGpu(viewProjectionInv),
                View = MatrixConvert.ToGpu(camera.View),
                SunColor = SunColor(),
                ShadowDistance = ShadowDistance,
                SkyAmbient = SkyAmbient(),
                SunFade = sunFade,
                MinLight = new Vector3(GraphicsSettings.Brightness, GraphicsSettings.Brightness, GraphicsSettings.Brightness),
                ShadowStrength = ShadowStrength,
                // Minimum light a dimension floods everywhere (0 in the Overworld; a small glow in a sunless one
                // so unlit ground isn't pitch black). Applied generically in the composition shader.
                AmbientFloor = _ambientFloor,
                // When shadows are disabled / not run this frame the resolve pass is skipped, leaving a stale
                // buffer; tell the shader to treat every sky-lit surface as fully lit, never sampling that buffer.
                ShadowsEnabled = (GraphicsSettings.ShadowsEnabled && shadowPass) ? 1f : 0f,
                CameraPos = camera.Position,
                DebugShadow = RenderDebug.ShadowFactor ? 1f : 0f,
                SunDirection = toSun,
                Time = (float) (_dayTimeSeconds % 3600.0),
                // Night-time moon glint on water: the moon sits opposite the sun, so it fades in as the sun
                // drops below the horizon (SunFade(-toSun.Y)), mirroring the sun's day fade.
                MoonColor = new Vector3(0.55f, 0.62f, 0.85f),
                MoonFade = SunFade(-toSun.Y),
                SkyColor = SkyZenithColor(),
                StarBrightness = StarBrightness(),
                HorizonColor = SkyHorizonColor(),
                SunSize = SunSize,
                VoidColor = SkyVoidColor(),
                MoonSize = MoonSize,
                SunsetColor = SunsetColor(),
                SkyDistance = horizonDistance + 48f,
                FogStart = horizonDistance * 0.72f,
                FogEnd = horizonDistance * 0.97f,
                Underwater = _underwater ? 1f : 0f,
                UnderwaterColor = _underwaterColor,
            });

            if (_compositionTexBind == null)
            {
                var gbuffer = ClientResources.GBuffer;
                var sun = (SunTexture ?? ClientResources.WhitePixel).View;
                var moon = (MoonTexture ?? ClientResources.WhitePixel).View;
                _compositionTexBind = new GpuBindGroup(_compositionTexLayout, new[]
                {
                    GpuBindGroup.Texture(0, gbuffer.Diffuse.View),
                    GpuBindGroup.Texture(1, gbuffer.Normal.View),
                    GpuBindGroup.Texture(2, gbuffer.Depth.View),
                    GpuBindGroup.Texture(3, gbuffer.Light.View),
                    GpuBindGroup.Texture(4, _shadowResolve.View),
                    GpuBindGroup.Texture(5, sun),
                    GpuBindGroup.Texture(6, moon),
                    GpuBindGroup.Sampler(7, GpuSamplers.Framebuffer),
                    GpuBindGroup.Sampler(8, GpuSamplers.Celestial),
                }, "compositionTex");
            }

            // Composition writes every pixel (background sky included), so the load op is irrelevant — clear.
            var color = ColorAttachment.ClearTo(Renderer.HdrScene.View, 0, 0, 0, 1);
            var pass = RenderPassBuilder.Begin(encoder, stackalloc[] { color });
            pass.SetPipeline(_compositionPipeline);
            pass.SetBindGroup(0, _compositionParamsBind);
            pass.SetBindGroup(1, _compositionTexBind);
            pass.Draw(3);
            pass.End();
            pass.Release();
        }
    }
}

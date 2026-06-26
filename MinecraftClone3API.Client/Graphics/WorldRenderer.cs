using System;
using System.Collections.Generic;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.Client.Blocks;
using MinecraftClone3API.Entities;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

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

        // Reused across frames so the per-frame visibility pass allocates nothing steady-state; cleared
        // at the top of DrawGeometryFramebuffer and grown to the working set. These three capacity-1024
        // reference-type lists, re-newed every frame, were the single largest main-thread allocator in a
        // trace (~11s of List<ChunkRenderData>..ctor → constant Gen0 GC pauses on the render thread).
        private static readonly List<ChunkRenderData> _chunksToDraw = new List<ChunkRenderData>(1024);
        private static readonly List<ChunkRenderData> _transparentSortedChunks = new List<ChunkRenderData>(1024);
        private static readonly List<ChunkRenderData> _transparentChunks = new List<ChunkRenderData>(1024);

        // Phase-2 distant-horizon LOD regions to draw this frame (the ring beyond the real-chunk render
        // distance). Main-thread scratch, refilled each frame like _chunksToDraw. _lodRenderDistance is the
        // block radius the LOD ring extends to (0 ⇒ LOD dormant), captured per frame from the world.
        private static readonly List<LodRenderData> _lodToDraw = new List<LodRenderData>(256);
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

        // Opaque chunks are drawn FRONT-TO-BACK (near first) so early-Z rejects occluded far fragments before
        // the costly 3-MRT + texture-array fragment shader runs — pure overdraw reduction, identical pixels
        // (depth resolves visibility either way). Without this the opaque list was in arbitrary RenderList
        // order, shading then overwriting hidden fragments on the fill-bound iGPU.
        private static readonly Comparison<ChunkRenderData> _opaqueSortNearFirst = (chunk1, chunk2)
            => ((_sortCameraPos - chunk1.Middle).LengthSquared).CompareTo(
                (_sortCameraPos - chunk2.Middle).LengthSquared);

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
        // applies next frame; the map FBO is recreated by EnsureShadowMap when the size changes.
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

        // Tracks the size the live ShadowFramebuffer was built at; EnsureShadowMap recreates the FBO when the
        // quality preset changes ShadowMapSize. Initialized to the size ClientResources builds it at.
        private static int _shadowMapSize = ShadowFramebuffer.ShadowMapSize;
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
        private static readonly List<ChunkRenderData> _shadowChunks = new List<ChunkRenderData>(1024);
        private static Matrix4 _lightViewProj;
        // World units per shadow texel (the shader scales its normal-offset bias by this).
        private static float _shadowTexelWorld;

        // Set by BuildVisibleSet: true iff a visible chunk within ShadowDistance is sky-exposed. Gates the
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
        private static float SunHeight() => MathF.Sin(DayTime() * MathHelper.TwoPi);

        /// <summary>Day factor (0 night .. 1 full day) used to cross-fade every time-of-day colour: 0 once the
        /// sun is well below the horizon, 1 once it is well above.</summary>
        private static float DayFactor() => MathHelper.Clamp((SunHeight() + 0.2f) / 0.4f, 0f, 1f);

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
            var day = MathHelper.Clamp((sunHeight + 0.2f) / 0.4f, 0f, 1f);
            var highness = MathHelper.Clamp(sunHeight, 0f, 1f);

            var horizon = new Vector3(1.0f, 0.5f, 0.25f);
            var noon = new Vector3(1.0f, 0.98f, 0.92f);
            var night = new Vector3(0.04f, 0.05f, 0.09f);

            var dayColor = Vector3.Lerp(horizon, noon, highness);
            return Vector3.Lerp(night, dayColor, day);
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
            return Vector3.Lerp(moon, sky, day);
        }

        /// <summary>Zenith colour of the background sky gradient: deep blue overhead by day, near-black at
        /// night. The composition shader blends it toward <see cref="SkyHorizonColor"/> down to the horizon.</summary>
        private static Vector3 SkyZenithColor()
        {
            if (!_hasSky) return _fogColor;
            var day = DayFactor();
            var night = new Vector3(0.00f, 0.01f, 0.03f);
            var sky = new Vector3(0.28f, 0.50f, 0.92f);
            return Vector3.Lerp(night, sky, day);
        }

        /// <summary>Horizon haze colour: a light hazy blue by day, dark at night. Doubles as the distance-fog
        /// colour in the composition shader so terrain melts into the horizon at the render-distance edge.</summary>
        private static Vector3 SkyHorizonColor()
        {
            if (!_hasSky) return _fogColor;
            var day = DayFactor();
            var night = new Vector3(0.01f, 0.02f, 0.05f);
            var sky = new Vector3(0.66f, 0.78f, 0.93f);
            return Vector3.Lerp(night, sky, day);
        }

        /// <summary>Colour below the horizon (the void): a dim grey-blue by day, near-black at night.</summary>
        private static Vector3 SkyVoidColor()
        {
            if (!_hasSky) return _fogColor;
            var day = DayFactor();
            var night = new Vector3(0.00f, 0.00f, 0.01f);
            var voidColor = new Vector3(0.18f, 0.22f, 0.28f);
            return Vector3.Lerp(night, voidColor, day);
        }

        /// <summary>Sunrise/sunset glow colour, strongest when the sun is near the horizon and fading to black
        /// by noon and deep night. The composition shader paints it as an orange band near the horizon in the
        /// sun's azimuth.</summary>
        private static Vector3 SunsetColor()
        {
            if (!_hasSky) return Vector3.Zero;
            var band = MathHelper.Clamp(1f - MathF.Abs(SunHeight()) / 0.30f, 0f, 1f);
            return new Vector3(0.85f, 0.42f, 0.16f) * band;
        }

        /// <summary>Star brightness (0 by day, up to 1 at night), faded by the same day factor as everything
        /// else so the stars come out smoothly as the sky darkens.</summary>
        private static float StarBrightness() => _hasSky ? MathHelper.Clamp(1f - DayFactor(), 0f, 1f) : 0f;

        /// <summary>Unit vector pointing from the world toward the sun, animated by the same day/night
        /// clock as <see cref="SunColor"/> so the brightest sun aligns with the highest sun. Drives the
        /// shadow map's orthographic sun camera; the normalized Y is the sun altitude (≤0 ⇒ below the
        /// horizon, shadow pass skipped). The small Z tilt keeps shadows off the world axes.</summary>
        private static Vector3 SunDirection()
        {
            var angle = DayTime() * MathHelper.TwoPi;
            return Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));
        }

        /// <summary>Directional sun-term strength (0..1) for the current sun altitude: a smoothstep that ramps
        /// the sun/shadow contribution down to zero as the sun reaches the horizon, so dusk fades smoothly to
        /// ambient. 0 at/below <see cref="ShadowFadeLow"/> (sun treated as down → the shadow pass is skipped).</summary>
        private static float SunFade(float sunY)
        {
            var t = MathHelper.Clamp((sunY - ShadowFadeLow) / (ShadowFadeHigh - ShadowFadeLow), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        public static void RenderWorld(WorldClient world, Matrix4 projection)
        {
            // Server-authoritative time of day; sampled once so every sun term this frame is consistent. The
            // benchmark pins it (FixedTimeOfDay) for reproducible sun/shadow conditions.
            _dayTimeSeconds = FixedTimeOfDay ?? world.WorldTimeSeconds;
            _hasSky = world.HasSky;
            _fogColor = world.FogColor;
            _ambientFloor = world.AmbientLight;

            var viewProjection = PlayerController.Camera.View * projection;
            var viewProjectionInv = viewProjection.Inverted();
            _viewFrustum.Set(viewProjection);
            var viewFrustum = _viewFrustum;

            var toSun = SunDirection();
            // The directional sun term -- and with it the shadows -- fade out together as the sun nears the
            // horizon, so dusk converges smoothly to ambient instead of the sun term popping when the shadow
            // pass cuts off. sunUp (term present) is exactly sunFade > 0.
            // A sunless dimension has no directional sun (so no shadow pass); it is lit by block light + the
            // ambient floor only.
            var sunFade = _hasSky ? SunFade(toSun.Y) : 0f;
            var sunUp = sunFade > 0f;

            // Per-pass GPU timing (F3 profiler shadowMs/geomMs/compMs); no-op unless recording.
            GpuTimers.Enabled = Profiler.Recording;
            GpuTimers.BeginFrame();

            // Build the visible set (frustum + render-distance scan): fills the draw lists and flags whether
            // any visible chunk is sky-exposed, so the shadow passes can be skipped when none is (deep cave).
            BuildVisibleSet(world, PlayerController.Camera, viewFrustum);
            _lodRenderDistance = world.LodRenderDistance;
            BuildLodVisibleSet(world, PlayerController.Camera, viewFrustum);

            RenderDebug.ShadowPass = sunUp && _anyShadowReceiver && GraphicsSettings.ShadowsEnabled;
            if (RenderDebug.ShadowPass)
            {
                GpuTimers.Begin(GpuTimers.Pass.Shadow);
                DrawShadowMap(world, PlayerController.Camera, projection, toSun);
                GpuTimers.End(GpuTimers.Pass.Shadow);
            }

            var wireframe = false;
            if (wireframe) GL.PolygonMode(TriangleFace.Front, PolygonMode.Line);

            GpuTimers.Begin(GpuTimers.Pass.Geometry);
            DrawGeometryFramebuffer(world, PlayerController.Camera, projection);
            GpuTimers.End(GpuTimers.Pass.Geometry);

            if (wireframe) GL.PolygonMode(TriangleFace.Front, PolygonMode.Fill);

            GpuTimers.Begin(GpuTimers.Pass.Composition);
            // Resolve the shadow PCF at half res first (the old per-pixel-at-full-res cost was ~45% of the
            // frame); composition then depth-aware-upsamples it. Gated like DrawShadowMap — when skipped the
            // resolve buffer is stale but composition's same early-outs never sample it.
            if (RenderDebug.ShadowPass) DrawShadowResolve(PlayerController.Camera, viewProjectionInv, sunFade);
            DrawComposition(PlayerController.Camera, viewProjectionInv, sunFade);
            GpuTimers.End(GpuTimers.Pass.Composition);

            GpuTimers.EndFrame();
        }

        /// <summary>Recreates the shadow framebuffer when the Shadow Quality preset changes its resolution
        /// (GL, main-thread — called inside the shadow pass). A no-op when the size already matches.</summary>
        private static void EnsureShadowMap()
        {
            if (_shadowMapSize == ShadowMapSize) return;

            _shadowMapSize = ShadowMapSize;
            ClientResources.ShadowFramebuffer?.Dispose();
            ClientResources.ShadowFramebuffer = new ShadowFramebuffer(_shadowMapSize);
        }

        /// <summary>Renders opaque chunk depth from the sun's orthographic point of view into the shadow
        /// framebuffer. Re-draws the already-uploaded chunk VAOs (no remesh, main-thread GL only); the
        /// shadow-resolve pass samples it to attenuate the sky/sun term. The map is fit to the bounding sphere
        /// of the [ShadowNear, ShadowDistance] view-frustum slice (radius constant as the camera rotates →
        /// stable size). Deliberately NOT texel-snapped — the sun advances every frame, so snapping to the
        /// rotating light-space texel grid would turn the shadow's smooth crawl into whole-texel flicker; the
        /// soft low-res PCF keeps camera-motion shimmer down instead. See CLAUDE.md.</summary>
        private static void DrawShadowMap(WorldClient world, Camera camera, Matrix4 projection, Vector3 toSun)
        {
            // Frustum half-angle tangents straight from the projection (robust to FOV/aspect/resize):
            // Row0.X = 1/tan(fovX/2), Row1.Y = 1/tan(fovY/2). k2 turns a slice depth z into its corner
            // radius z*sqrt(k2), used for the analytic bounding sphere below.
            var tanHalfH = 1f / projection.Row0.X;
            var tanHalfV = 1f / projection.Row1.Y;
            var k2 = tanHalfH * tanHalfH + tanHalfV * tanHalfV;

            // LookAt up vector; the UnitZ fallback only triggers if the sun is nearly straight up (never,
            // given SunDirection's permanent Z tilt).
            var up = MathF.Abs(toSun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

            GraphicsDebug.PushGroup("Shadow");

            EnsureShadowMap();

            // Cull disabled: the voxel mesher only emits exposed (single-sided) faces, so there are no back
            // faces — culling would drop the sun-facing surfaces. Polygon offset pushes shadow depth back to
            // fight self-shadowing acne (paired with the normal-offset + depth bias in the resolve shader).
            RenderState.Set(new GlState {CullFace = false, DepthTest = true, DepthFunc = DepthFunction.Lequal});
            GL.Enable(EnableCap.PolygonOffsetFill);
            GL.PolygonOffset(2f, 4f);

            var shader = ClientResources.ShadowDepthShader;
            shader.Bind();
            var uLightViewProj = shader.GetUniformLocation("uLightViewProj");

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
            var lightView = Matrix4.LookAt(lightEye, center, up);
            var lightProj = Matrix4.CreateOrthographic(2f * radius, 2f * radius, 0f, distBack + radius);
            _lightViewProj = lightView * lightProj;
            _shadowFrustum.Set(_lightViewProj);

            var shadowChunks = _shadowChunks;
            shadowChunks.Clear();
            var renderList = world.RenderList;
            for (var i = 0; i < renderList.Count; i++)
            {
                var renderData = renderList[i];
                // Transparent chunks don't cast shadows (a solid shadow from translucent geometry is wrong);
                // skip chunks with no opaque geometry entirely (they'd contribute zero-index sub-draws).
                if (renderData.HasTransparency || !renderData.HasOpaque) continue;
                if (!_shadowFrustum.SpehereIntersection(renderData.Middle, Chunk.Radius)) continue;
                shadowChunks.Add(renderData);
            }

            ClientResources.ShadowFramebuffer.Bind();
            GL.Clear(ClearBufferMask.DepthBufferBit);
            GL.UniformMatrix4(uLightViewProj, false, ref _lightViewProj);

            // One batched multidraw over the shadow casters' shared arena sub-ranges (position-only VAO).
            world.OpaqueArena.Draw(shadowChunks, true);

            GL.Disable(EnableCap.PolygonOffsetFill);
            ClientResources.ShadowFramebuffer.Unbind(ClientResources.Window.FramebufferSize.X, ClientResources.Window.FramebufferSize.Y);
            GraphicsDebug.PopGroup();
        }

        /// <summary>Fills the opaque/transparent draw lists for this frame — a linear scan of every loaded
        /// chunk, frustum- and render-distance-culled — and flags whether the sun shadow passes are needed
        /// (<see cref="_anyShadowReceiver"/>: true iff a visible sky-exposed chunk is within ShadowDistance).</summary>
        private static void BuildVisibleSet(WorldClient world, Camera camera, Frustum viewFrustum)
        {
            _chunksToDraw.Clear();
            _transparentSortedChunks.Clear();
            _transparentChunks.Clear();
            _anyShadowReceiver = false;

            // Snapshot the live (settings-driven) distances once so the per-chunk loop reads locals, not the
            // property getters, each iteration.
            var renderDistanceSq = RenderDistanceSq;
            var shadowDistanceSq = ShadowDistanceSq;

            var renderList = world.RenderList;
            for (var i = 0; i < renderList.Count; i++)
            {
                var renderData = renderList[i];
                var chunkMiddle = renderData.Middle;
                // Distance cull FIRST (one subtract + dot) so the ~3000 loaded-but-out-of-render-distance
                // chunks at high render distance are rejected before the 6-plane frustum test.
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
                else _chunksToDraw.Add(renderData);

                if (renderData.SkyExposed && lengthSq <= shadowDistanceSq) _anyShadowReceiver = true;
            }

            // Sort the near transparent chunks back-to-front, then build the final opaque draw list as
            // opaque + all transparent (so a transparent chunk's opaque faces are drawn in the opaque pass).
            _sortCameraPos = camera.Position;
            _transparentSortedChunks.Sort(_transparentSort);
            _transparentChunks.AddRange(_transparentSortedChunks);
            _chunksToDraw.AddRange(_transparentChunks);

            RenderDebug.DrawnChunks = _chunksToDraw.Count;
        }

        /// <summary>Frustum + distance cull of the Phase-2 LOD regions into <see cref="_lodToDraw"/>. Keeps any
        /// region overlapping the ring [RenderDistance, LodRenderDistance] (nearest/farthest-point test so the
        /// boundary never gaps); the geometry pass draws them under the real chunks (DepthFunc.Less), so the
        /// inner overlap is hidden behind loaded chunks. Empty (no work) when the LOD horizon is dormant.</summary>
        private static void BuildLodVisibleSet(WorldClient world, Camera camera, Frustum viewFrustum)
        {
            _lodToDraw.Clear();
            var far = _lodRenderDistance;
            // Inner edge pulled in by the fade band so the horizon has geometry to dither IN against the chunks
            // dithering OUT (else the band would be chunk-discard against empty horizon = holes).
            var near = RenderDistance - FadeBandWidth;
            if (far <= RenderDistance || world.ForceLodOff) { RenderDebug.LodDrawn = 0; return; }

            var list = world.LodRenderList;
            for (var i = 0; i < list.Count; i++)
            {
                var rd = list[i];
                var dist = (camera.Position - rd.Middle).Length;
                if (dist - rd.Radius > far) continue;        // entirely beyond the LOD horizon
                if (dist + rd.Radius < near) continue;        // entirely inside the real-chunk core
                if (!viewFrustum.SpehereIntersection(rd.Middle, rd.Radius)) continue;
                _lodToDraw.Add(rd);
            }

            RenderDebug.LodDrawn = _lodToDraw.Count;
        }

        private static void DrawGeometryFramebuffer(WorldClient world, Camera camera, Matrix4 projection)
        {
            RenderState.Set(new GlState {CullFace = true, DepthTest = true, DepthFunc = DepthFunction.Lequal});

            GraphicsDebug.PushGroup("Geometry");
            ClientResources.GeometryFramebuffer.Bind();
            ClientResources.GeometryFramebuffer.Clear(Color4.DarkBlue);
            //ClientResources.GeometryFramebuffer.Clear(Color4.Transparent);    Breaks transparency

            var shader = ClientResources.WorldGeometryShader;
            shader.Bind();
            // Uniform/sampler locations are queried by name (GLSL 4.10 has no explicit
            // layout(location=)/layout(binding=) for uniforms; macOS caps OpenGL at 4.1).
            var uCutoff = shader.GetUniformLocation("uCutoff");
            GL.UniformMatrix4(shader.GetUniformLocation("uView"), false, ref camera.View);
            GL.UniformMatrix4(shader.GetUniformLocation("uProjection"), false, ref projection);
            GL.Uniform1(uCutoff, 1);
            GL.Uniform1(shader.GetUniformLocation("uTextures16"), 0);
            GL.Uniform1(shader.GetUniformLocation("uTextures64"), 1);
            GL.Uniform1(shader.GetUniformLocation("uTextures256"), 2);
            GL.Uniform1(shader.GetUniformLocation("uTextures1024"), 3);

            // LOD cross-fade band: full-detail chunks dissolve into the horizon over [RD - FadeBandWidth, RD].
            // Disabled (start past the far plane) when the horizon is dormant, so chunks never fade into nothing.
            var uFadeMode = shader.GetUniformLocation("uFadeMode");
            var fadeActive = _lodRenderDistance > RenderDistance;
            GL.Uniform3(shader.GetUniformLocation("uCameraPos"), camera.Position.X, camera.Position.Y, camera.Position.Z);
            GL.Uniform1(shader.GetUniformLocation("uFadeStart"), fadeActive ? RenderDistance - FadeBandWidth : 1e9f);
            GL.Uniform1(shader.GetUniformLocation("uFadeEnd"), fadeActive ? RenderDistance : 1e9f + 1f);

            GlTextureUploader.Bind();
            Samplers.BindBlockTextureSampler();

            // Draw all opaque chunk geometry (incl. the opaque faces of transparent chunks) front-to-back in
            // ONE batched multidraw over the shared arena (positions are baked world-space → no per-chunk matrix).
            // uFadeMode 0 = these near chunks fade OUT across the band.
            GL.Uniform1(uFadeMode, 0);
            GraphicsDebug.PushGroup("Opaque");
            world.OpaqueArena.Draw(_chunksToDraw, false);
            GraphicsDebug.PopGroup();

            // Phase-2 distant horizon: draw the LOD ring UNDER the real chunks — DepthFunc.Less means an
            // already-drawn (nearer) real chunk wins, so the inner overlap is hidden and the streaming frontier
            // never holes or double-images. Same shader + G-buffer, just coarse baked-light geometry past the
            // render distance. Restore Lequal so RenderState's tracked state stays consistent.
            if (_lodToDraw.Count > 0)
            {
                GraphicsDebug.PushGroup("LodHorizon");
                GL.Uniform1(uFadeMode, 1);   // LOD fades IN across the band (complementary to the chunks)
                GL.DepthFunc(DepthFunction.Less);
                world.LodArena.Draw(_lodToDraw, false);
                GL.DepthFunc(DepthFunction.Lequal);
                GL.Uniform1(uFadeMode, 0);   // restore for the transparent chunk pass (near → no fade)
                GraphicsDebug.PopGroup();
            }

            RenderState.Set(new GlState
            {
                CullFace = true, DepthTest = true, DepthFunc = DepthFunction.Lequal,
                Blend = true, BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            });
            // Blend only the diffuse attachment (translucency); the normal+light attachments must be written
            // cleanly by the front-most transparent surface so the water flag (normal.w) and the surface's own
            // light survive instead of blending toward the background and becoming unreadable in composition.
            // Restored right after so RenderState's single Blend bool stays the whole per-buffer description.
            GL.Disable(IndexedEnableCap.Blend, 1);
            GL.Disable(IndexedEnableCap.Blend, 2);
            GL.Uniform1(uCutoff, 0);

            //Draw transparent blocks back to front (per-chunk: each needs its own back-to-front face sort)
            GraphicsDebug.PushGroup("Transparent");
            foreach (var chunk in _transparentChunks)
                chunk.DrawTransparent();
            GraphicsDebug.PopGroup();

            GL.Enable(IndexedEnableCap.Blend, 1);
            GL.Enable(IndexedEnableCap.Blend, 2);

            RenderState.Set(new GlState {CullFace = true, DepthTest = true, DepthFunc = DepthFunction.Lequal});

            GraphicsDebug.PushGroup("Overlays");
            EntityRenderer.Render(world, camera, projection);
            PlayerController.Render(camera, projection);
            ChunkBorderRenderer.Render(camera, projection);
            GraphicsDebug.PopGroup();

            ClientResources.GeometryFramebuffer.Unbind(ClientResources.Window.FramebufferSize.X, ClientResources.Window.FramebufferSize.Y);
            GraphicsDebug.PopGroup();
        }

        private static void DrawLightFramebuffer(WorldServer world, Matrix4 viewProjectionInv, Frustum viewFrustum)
        {
            //TODO: Lighting?

            /*
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactorSrc.One, BlendingFactorDest.One);

            ClientResources.LightFramebuffer.Bind();
            ClientResources.LightFramebuffer.Clear(Color4.Black);

            ClientResources.PointLightShader.Bind();
            GL.UniformMatrix4(0, false, ref viewProjectionInv);

            
            foreach (var light in world.Lights.Select(light => light as PointLight))
            {
                if (light == null || !viewFrustum.SpehereIntersection(light.Position, light.Range)) continue;
                GL.Uniform3(4, light.Position);
                GL.Uniform3(5, light.Color);
                GL.Uniform1(6, light.Range);
                ClientResources.ScreenRectVao.Draw();
            }
            
            ClientResources.LightFramebuffer.Unbind(Program.Window.FramebufferSize.X, Program.Window.FramebufferSize.Y);

            GL.Disable(EnableCap.Blend);
            */
        }

        /// <summary>Resolves the sun shadow at HALF resolution into <see cref="ClientResources.
        /// ShadowResolveFramebuffer"/>: a fullscreen quad runs the 12-tap PCF (moved out of the composition
        /// pass) per half-res pixel, writing the shadow factor + normalized depth. Composition then
        /// depth-aware-upsamples it. This trades the full-res PCF (~45% of the GPU frame) for a quarter-count
        /// PCF + a cheap upsample. Reads the G-buffer normal/depth/light + the shadow depth map.</summary>
        private static void DrawShadowResolve(Camera camera, Matrix4 viewProjectionInv, float sunFade)
        {
            GraphicsDebug.PushGroup("ShadowResolve");
            var fb = ClientResources.ShadowResolveFramebuffer;
            fb.Bind();
            // Fullscreen quad: no depth test, no culling.
            RenderState.Set(new GlState {CullFace = false, DepthTest = false});

            ClientResources.GeometryFramebuffer.BindTexturesAndSamplers();
            ClientResources.ShadowFramebuffer.BindDepthTexture(TextureUnit.Texture4);
            var sh = ClientResources.ShadowResolveShader;
            sh.Bind();
            GL.Uniform1(sh.GetUniformLocation("uNormal"), 1);
            GL.Uniform1(sh.GetUniformLocation("uDepth"), 2);
            GL.Uniform1(sh.GetUniformLocation("uLight"), 3);
            GL.Uniform1(sh.GetUniformLocation("uShadowMap"), 4);
            GL.UniformMatrix4(sh.GetUniformLocation("uViewProjectionInv"), false, ref viewProjectionInv);
            GL.UniformMatrix4(sh.GetUniformLocation("uView"), false, ref camera.View);
            GL.UniformMatrix4(sh.GetUniformLocation("uLightViewProj"), false, ref _lightViewProj);
            GL.Uniform1(sh.GetUniformLocation("uShadowTexel"), _shadowTexelWorld);
            GL.Uniform1(sh.GetUniformLocation("uShadowMapTexel"), 1f / _shadowMapSize);
            GL.Uniform1(sh.GetUniformLocation("uShadowDistance"), ShadowDistance);
            GL.Uniform1(sh.GetUniformLocation("uSunFade"), sunFade);
            GL.Uniform1(sh.GetUniformLocation("uShadowSoftness"), ShadowSoftness);
            GL.Uniform1(sh.GetUniformLocation("uShadowsEnabled"), GraphicsSettings.ShadowsEnabled ? 1f : 0f);
            ClientResources.ScreenRectVao.Draw();

            fb.Unbind(ClientResources.Window.FramebufferSize.X, ClientResources.Window.FramebufferSize.Y);
            GraphicsDebug.PopGroup();
        }

        private static void DrawComposition(Camera camera, Matrix4 viewProjectionInv, float sunFade)
        {
            GraphicsDebug.PushGroup("Composition");
            GL.ClearColor(Color4.DarkBlue);
            GL.Clear(ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit | ClearBufferMask.ColorBufferBit);

            //GL.Enable(EnableCap.Blend);

            // BindTexturesAndSamplers binds the geometry light buffer (baked block lighting) to
            // unit 3, so the separate deferred LightFramebuffer is no longer bound here.
            ClientResources.GeometryFramebuffer.BindTexturesAndSamplers();
            // Resolved half-res sun shadow on unit 4 (units 0-3 are the G-buffer). Sampled with texelFetch, so
            // clear any sampler object on the unit. uSunFade (0..1) scales the whole directional sun term and
            // gates shadow sampling; it is 0 when the sun is down (the shadow passes were skipped) so the
            // shader treats everything as lit-by-block-light-and-ambient and never reads the stale buffer.
            GL.ActiveTexture(TextureUnit.Texture4);
            GL.BindTexture(TextureTarget.Texture2D, ClientResources.ShadowResolveFramebuffer.Texture.Id);
            GL.BindSampler(4, 0);
            var comp = ClientResources.CompositionShader;
            comp.Bind();
            GL.Uniform1(comp.GetUniformLocation("uDiffuse"), 0);
            GL.Uniform1(comp.GetUniformLocation("uNormal"), 1);
            GL.Uniform1(comp.GetUniformLocation("uDepth"), 2);
            GL.Uniform1(comp.GetUniformLocation("uLight"), 3);
            GL.Uniform1(comp.GetUniformLocation("uShadowResolved"), 4);
            GL.Uniform1(comp.GetUniformLocation("uShadowDistance"), ShadowDistance);
            GL.UniformMatrix4(comp.GetUniformLocation("uViewProjectionInv"), false, ref viewProjectionInv);
            GL.UniformMatrix4(comp.GetUniformLocation("uView"), false, ref camera.View);
            GL.Uniform1(comp.GetUniformLocation("uSunFade"), sunFade);
            GL.Uniform1(comp.GetUniformLocation("uShadowStrength"), ShadowStrength);
            // When shadows are disabled the resolve pass is skipped, leaving a stale buffer bound; this tells
            // the shader to treat every sky-lit surface as fully lit instead of sampling that stale buffer.
            GL.Uniform1(comp.GetUniformLocation("uShadowsEnabled"), GraphicsSettings.ShadowsEnabled ? 1f : 0f);
            GL.Uniform1(comp.GetUniformLocation("uDebugShadow"), RenderDebug.ShadowFactor ? 1f : 0f);
            GL.Uniform3(comp.GetUniformLocation("uMinLight"), GraphicsSettings.Brightness,
                GraphicsSettings.Brightness, GraphicsSettings.Brightness);
            // Minimum light a dimension floods everywhere (0 in the Overworld; a small glow in a sunless one so
            // unlit ground isn't pitch black). Applied generically in the composition shader.
            GL.Uniform3(comp.GetUniformLocation("uAmbientFloor"), _ambientFloor.X, _ambientFloor.Y, _ambientFloor.Z);
            var sun = SunColor();
            GL.Uniform3(comp.GetUniformLocation("uSunColor"), sun.X, sun.Y, sun.Z);
            var skyAmbient = SkyAmbient();
            GL.Uniform3(comp.GetUniformLocation("uSkyAmbient"), skyAmbient.X, skyAmbient.Y, skyAmbient.Z);
            // Camera position + sun direction + a wrapped wave-scroll time. Shared by the background sky (the
            // view-ray reconstruction) and the water surface (Fresnel sky reflection + sun specular + animated
            // normals reconstruct the view vector from the same uniforms). uTime wraps at 3600 s for precision.
            var toSun = SunDirection();
            GL.Uniform3(comp.GetUniformLocation("uCameraPos"), camera.Position.X, camera.Position.Y, camera.Position.Z);
            GL.Uniform3(comp.GetUniformLocation("uSunDirection"), toSun.X, toSun.Y, toSun.Z);
            GL.Uniform1(comp.GetUniformLocation("uTime"), (float) (_dayTimeSeconds % 3600.0));
            // Night-time moon glint on water: the moon sits opposite the sun, so it fades in (uMoonFade) as the
            // sun drops below the horizon, mirroring uSunFade for the day. The cool tint is the moonlight colour
            // the water specular reflects (the sun's own specular is gated to daytime by uSunFade).
            GL.Uniform1(comp.GetUniformLocation("uMoonFade"), SunFade(-toSun.Y));
            GL.Uniform3(comp.GetUniformLocation("uMoonColor"), 0.55f, 0.62f, 0.85f);

            // Background sky: the gradient/sunset/sun/moon/stars are rendered procedurally per background pixel
            // in the composition shader (no extra geometry, no remesh — same fullscreen pass) and water reflects
            // the same SkyColor. All it needs are the time-of-day colours computed here and the pack sun/moon
            // textures on units 5/6.
            var skyColor = SkyZenithColor();
            GL.Uniform3(comp.GetUniformLocation("uSkyColor"), skyColor.X, skyColor.Y, skyColor.Z);
            var horizon = SkyHorizonColor();
            GL.Uniform3(comp.GetUniformLocation("uHorizonColor"), horizon.X, horizon.Y, horizon.Z);
            var voidColor = SkyVoidColor();
            GL.Uniform3(comp.GetUniformLocation("uVoidColor"), voidColor.X, voidColor.Y, voidColor.Z);
            var sunset = SunsetColor();
            GL.Uniform3(comp.GetUniformLocation("uSunsetColor"), sunset.X, sunset.Y, sunset.Z);
            GL.Uniform1(comp.GetUniformLocation("uStarBrightness"), StarBrightness());
            GL.Uniform1(comp.GetUniformLocation("uSunSize"), SunSize);
            GL.Uniform1(comp.GetUniformLocation("uMoonSize"), MoonSize);
            // Far-plane (sky) vs terrain split + distance fog. When the Phase-2 LOD horizon is active the drawn
            // geometry reaches LodRenderDistance (well past RenderDistance), so the sky-distance threshold and
            // the fog band move out to that horizon — otherwise the LOD ring would be painted as sky (depth ≥
            // uSkyDistance) or fully fogged out. The fog melts the coarse far ring into uHorizonColor right
            // before the sky takes over, hiding the LOD edge. Dormant LOD ⇒ this is just RenderDistance as before.
            var horizonDistance = _lodRenderDistance > RenderDistance ? _lodRenderDistance : RenderDistance;
            GL.Uniform1(comp.GetUniformLocation("uSkyDistance"), horizonDistance + 48f);
            GL.Uniform1(comp.GetUniformLocation("uFogStart"), horizonDistance * 0.72f);
            GL.Uniform1(comp.GetUniformLocation("uFogEnd"), horizonDistance * 0.97f);

            GL.Uniform1(comp.GetUniformLocation("uSunTexture"), 5);
            GL.Uniform1(comp.GetUniformLocation("uMoonTexture"), 6);
            GL.ActiveTexture(TextureUnit.Texture5);
            GL.BindTexture(TextureTarget.Texture2D, (SunTexture ?? ClientResources.WhitePixel).Id);
            Samplers.BindCelestialSampler(5);
            GL.ActiveTexture(TextureUnit.Texture6);
            GL.BindTexture(TextureTarget.Texture2D, (MoonTexture ?? ClientResources.WhitePixel).Id);
            Samplers.BindCelestialSampler(6);

            ClientResources.ScreenRectVao.Draw();
            GraphicsDebug.PopGroup();

            //GL.Disable(EnableCap.Blend);
        }
    }
}
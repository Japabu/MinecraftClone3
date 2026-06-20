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
        //Changable in settings
        public const float RenderDistance = 256;
        public const float RenderDistanceSq = RenderDistance * RenderDistance;

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

        // Read by the cached transparent-sort comparator so the delegate is allocated once instead of
        // capturing a fresh closure (over camera position) every frame.
        private static Vector3 _sortCameraPos;
        private static readonly Comparison<ChunkRenderData> _transparentSort = (chunk1, chunk2)
            => (int) ((_sortCameraPos - chunk2.Middle).LengthSquared * 1000 -
                      (_sortCameraPos - chunk1.Middle).LengthSquared * 1000);

        // Refilled in place each frame instead of allocating a fresh Plane[6] + 6 planes per call.
        private static readonly Frustum _viewFrustum = new Frustum();

        // Cascaded sun shadow maps. The camera view frustum is split into CascadeCount distance slices
        // ([ShadowNear, ShadowDistance], partitioned by the practical log/uniform blend below); each slice
        // gets its own depth layer tightly fit to its bounding sphere, so near cascades are crisp and far
        // cascades cover more ground at lower resolution. Geometry past ShadowDistance casts no shadow
        // (faded out smoothly). ShadowDistance is the main coverage knob; raising it (or CascadeCount /
        // ShadowMapSize) costs extra depth passes — see CLAUDE.md.
        //
        // MaxCascadeCount sizes the fixed scratch + the shadow framebuffer's layers; CascadeCount is the
        // RUNTIME active count (the GraphicsSettings shadow-quality knob, clamped to [1, Max]), read each
        // frame so it can change with no realloc / shader recompile. Both the depth-pass loop and the
        // composition's uCascadeCount run to it, so dropping it trims depth passes for FPS.
        public const int MaxCascadeCount = ShadowFramebuffer.MaxCascadeCount;
        public static int CascadeCount => Math.Clamp(GraphicsSettings.ShadowCascades, 1, MaxCascadeCount);
        public const float ShadowNear = 0.5f;
        public const float ShadowDistance = 160f;
        // Split distribution: 0 = uniform (even far cascades), 1 = logarithmic (tight near cascades). The
        // blend keeps near detail without starving the far cascade.
        private const float CascadeSplitLambda = 0.85f;
        // Pull each cascade's light eye this far past its bounding sphere toward the sun so casters just
        // outside the slice (e.g. a tall block) still shadow into it.
        private const float ShadowCasterExtent = 64f;

        // Shadow look knobs (uploaded as composition uniforms each frame, so tunable here with no shader
        // recompile). ShadowStrength (0..1): how dark a fully-shadowed surface goes — 1 = can reach black,
        // lower lifts the floor so sun shadows aren't crushed (only the direct-sun term, not ambient/caves).
        // ShadowSoftness: PCF penumbra radius in shadow texels — larger = softer, at no extra tap cost.
        public static float ShadowStrength = 0.65f;
        public static float ShadowSoftness = 8f;

        // Sun-altitude band (toSun.Y) over which the directional sun term -- and with it the shadows -- fade
        // in/out. Below ShadowFadeLow the sun is treated as down (shadow pass skipped, term zero). Fading the
        // whole sun term (not just toggling shadows) makes lit areas dim down to meet the shadowed ones at
        // dusk, so there is no brightening pop when the shadow pass cuts off. See CLAUDE.md.
        private const float ShadowFadeLow = 0.05f;
        private const float ShadowFadeHigh = 0.15f;

        // Per-cascade RenderDoc debug-group names, precomputed so the shadow pass allocates no strings.
        private static readonly string[] _cascadeGroupNames = new string[MaxCascadeCount];

        private static readonly Frustum[] _shadowFrusta = new Frustum[MaxCascadeCount];
        private static readonly List<ChunkRenderData> _shadowChunks = new List<ChunkRenderData>(1024);
        private static readonly Matrix4[] _lightViewProj = new Matrix4[MaxCascadeCount];
        // Flattened row-major copy of _lightViewProj for the mat4[] uniform upload (same byte layout OpenTK
        // marshals a single ref Matrix4 with, so transpose=false behaves identically per element).
        private static readonly float[] _lightViewProjFlat = new float[MaxCascadeCount * 16];
        // Far view-space distance of each cascade (shader cascade selection) and world units per shadow
        // texel of each cascade (shader normal-offset bias scales with it).
        private static readonly float[] _cascadeSplitsView = new float[MaxCascadeCount];
        private static readonly float[] _cascadeTexelWorld = new float[MaxCascadeCount];

        static WorldRenderer()
        {
            for (var i = 0; i < MaxCascadeCount; i++)
            {
                _shadowFrusta[i] = new Frustum();
                _cascadeGroupNames[i] = "Cascade " + i;
            }
        }

        // Set by BuildVisibleSet: true iff a visible chunk within ShadowDistance is sky-exposed. Gates the
        // sun shadow passes — deep in a cave no visible chunk is sky-lit, so the four cascade depth passes
        // (a fixed per-frame cost, see CLAUDE.md) are skipped; at a cave mouth the BFS reaches the sky-lit
        // terrain and they run again.
        private static bool _anyShadowReceiver;

        // Distance past which a sky-exposed chunk no longer forces the shadow passes (matches the composition
        // shader, which early-outs shadow sampling past the last cascade).
        public const float ShadowDistanceSq = ShadowDistance * ShadowDistance;

        // Render-time visibility BFS scratch (main-thread only, reused → zero per-frame alloc). Visited is
        // keyed on chunk position only, so the reached set is a strict superset of the truly-visible set
        // (it can over-draw, never hide). The queue element is a struct, so no per-node GC.
        private static readonly HashSet<Vector3i> _bfsVisited = new HashSet<Vector3i>();
        private static readonly Queue<(Vector3i Pos, int EntryFace)> _bfsQueue =
            new Queue<(Vector3i, int)>();
        private static readonly Vector3i[] _bfsOffsets =
        {
            new Vector3i(-1, 0, 0), new Vector3i(+1, 0, 0),
            new Vector3i(0, -1, 0), new Vector3i(0, +1, 0),
            new Vector3i(0, 0, -1), new Vector3i(0, 0, +1)
        };

        // Client-side day/night clock. The sun colour drives the sky-light term in the composition shader,
        // so the whole world brightens/dims over the cycle with no remesh. Real-time based (frame-rate
        // independent); multiplayer clients are not yet time-synced (see CLAUDE.md). Starts at noon.
        private const float DayLengthSeconds = 240f;
        private static readonly System.Diagnostics.Stopwatch _dayClock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>Sun colour/intensity for the current time of day: bright warm white at noon, orange at
        /// the horizon (sunrise/sunset), a dim blue at night. Multiplied by the baked sky factor in the
        /// composition shader.</summary>
        private static Vector3 SunColor()
        {
            var t = 0.25f + (float) (_dayClock.Elapsed.TotalSeconds / DayLengthSeconds);
            var sunHeight = MathF.Sin(t * MathHelper.TwoPi);

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
            var t = 0.25f + (float) (_dayClock.Elapsed.TotalSeconds / DayLengthSeconds);
            var sunHeight = MathF.Sin(t * MathHelper.TwoPi);
            var day = MathHelper.Clamp((sunHeight + 0.2f) / 0.4f, 0f, 1f);

            var moon = new Vector3(0.05f, 0.06f, 0.11f);
            var sky = new Vector3(0.12f, 0.15f, 0.20f);
            return Vector3.Lerp(moon, sky, day);
        }

        /// <summary>Unit vector pointing from the world toward the sun, animated by the same day/night
        /// clock as <see cref="SunColor"/> so the brightest sun aligns with the highest sun. Drives the
        /// shadow map's orthographic sun camera; the normalized Y is the sun altitude (≤0 ⇒ below the
        /// horizon, shadow pass skipped). The small Z tilt keeps shadows off the world axes.</summary>
        private static Vector3 SunDirection()
        {
            var t = 0.25f + (float) (_dayClock.Elapsed.TotalSeconds / DayLengthSeconds);
            var angle = t * MathHelper.TwoPi;
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
            var viewProjection = PlayerController.Camera.View * projection;
            var viewProjectionInv = viewProjection.Inverted();
            _viewFrustum.Set(viewProjection);
            var viewFrustum = _viewFrustum;

            var toSun = SunDirection();
            // The directional sun term -- and with it the shadows -- fade out together as the sun nears the
            // horizon, so dusk converges smoothly to ambient instead of the sun term popping when the shadow
            // pass cuts off. sunUp (term present) is exactly sunFade > 0.
            var sunFade = SunFade(toSun.Y);
            var sunUp = sunFade > 0f;

            // Per-pass GPU timing (F3 profiler shadowMs/geomMs/compMs); no-op unless recording.
            GpuTimers.Enabled = Profiler.Recording;
            GpuTimers.BeginFrame();

            // Occlusion cull from the camera chunk first: it fills the draw lists and flags whether any
            // visible chunk is sky-exposed, so the shadow passes can be skipped when none is (deep cave).
            BuildVisibleSet(world, PlayerController.Camera, viewFrustum);

            RenderDebug.ShadowPass = sunUp && _anyShadowReceiver && GraphicsSettings.Shadows;
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
            // Resolve the cascaded PCF at half res first (the old per-pixel-at-full-res cost was ~45% of the
            // frame); composition then depth-aware-upsamples it. Gated like DrawShadowMap — when skipped the
            // resolve buffer is stale but composition's same early-outs never sample it.
            if (RenderDebug.ShadowPass) DrawShadowResolve(PlayerController.Camera, viewProjectionInv, sunFade);
            DrawComposition(PlayerController.Camera, viewProjectionInv, sunFade);
            GpuTimers.End(GpuTimers.Pass.Composition);

            GpuTimers.EndFrame();
        }

        /// <summary>Split distance i of the cascade partition of [ShadowNear, ShadowDistance]: a blend of a
        /// logarithmic and a uniform split (the standard "practical split scheme"). i in [0, CascadeCount].</summary>
        private static float CascadeSplit(int i)
        {
            var p = (float) i / CascadeCount;
            var logSplit = ShadowNear * MathF.Pow(ShadowDistance / ShadowNear, p);
            var uniformSplit = ShadowNear + (ShadowDistance - ShadowNear) * p;
            return MathHelper.Lerp(uniformSplit, logSplit, CascadeSplitLambda);
        }

        /// <summary>Renders opaque chunk depth from the sun's orthographic point of view into each cascade
        /// layer of the shadow framebuffer. Re-draws the already-uploaded chunk VAOs (no remesh, main-thread
        /// GL only); the composition pass picks a cascade per pixel and samples it to attenuate the sky/sun
        /// term. Each cascade is fit to the bounding sphere of its view-frustum slice (radius constant per
        /// cascade as the camera rotates → stable size) and texel-snapped (no shimmer as the player moves).</summary>
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

            var forward = camera.Forward;
            var camPos = camera.Position;

            // Cull disabled: the voxel mesher only emits exposed (single-sided) faces, so there are no back
            // faces — culling would drop the sun-facing surfaces. Polygon offset pushes shadow depth back to
            // fight self-shadowing acne (paired with the normal-offset + depth bias in the composition shader).
            RenderState.Set(new GlState {CullFace = false, DepthTest = true, DepthFunc = DepthFunction.Lequal});
            GL.Enable(EnableCap.PolygonOffsetFill);
            GL.PolygonOffset(2f, 4f);

            var shader = ClientResources.ShadowDepthShader;
            shader.Bind();
            var uWorld = shader.GetUniformLocation("uWorld");
            var uLightViewProj = shader.GetUniformLocation("uLightViewProj");

            for (var c = 0; c < CascadeCount; c++)
            {
                var n = CascadeSplit(c);
                var f = CascadeSplit(c + 1);
                _cascadeSplitsView[c] = f;

                // Analytic bounding sphere of the [n, f] frustum slice. Centre is on the camera forward axis
                // (so only the camera position, not its orientation, moves it); radius depends solely on n, f
                // and the FOV, so it is constant across frames -> no rotation shimmer. When the sphere through
                // both corner rings would sit past the far plane, the far ring's circumscribed sphere is tighter.
                var zc = (n + f) * (k2 + 1f) * 0.5f;
                float radius;
                if (zc >= f) { zc = f; radius = f * MathF.Sqrt(k2); }
                else radius = MathF.Sqrt(n * n * k2 + (n - zc) * (n - zc));

                var center = camPos + forward * zc;
                _cascadeTexelWorld[c] = 2f * radius / ShadowFramebuffer.ShadowMapSize;

                // Deliberately NOT texel-snapped. The sun advances every frame (day/night cycle), so snapping
                // the projection to the rotating light-space texel grid turns the shadow's smooth crawl into
                // frequent whole-texel jumps -- the flicker. Unsnapped, the projection follows the sun
                // smoothly; the 6x6-effective PCF keeps any camera-motion shimmer soft. See CLAUDE.md.
                var distBack = radius + ShadowCasterExtent;
                var lightEye = center + toSun * distBack;
                var lightView = Matrix4.LookAt(lightEye, center, up);
                var lightProj = Matrix4.CreateOrthographic(2f * radius, 2f * radius, 0f, distBack + radius);
                var lightViewProj = lightView * lightProj;
                _lightViewProj[c] = lightViewProj;
                _shadowFrusta[c].Set(lightViewProj);
                FlattenMatrix(lightViewProj, _lightViewProjFlat, c * 16);

                var shadowChunks = _shadowChunks;
                shadowChunks.Clear();
                var renderList = world.RenderList;
                for (var i = 0; i < renderList.Count; i++)
                {
                    var renderData = renderList[i];
                    if (renderData.HasTransparency) continue;
                    var chunkMiddle = (renderData.Chunk.Position * Chunk.Size + new Vector3i(Chunk.Size / 2)).ToVector3();
                    if (!_shadowFrusta[c].SpehereIntersection(chunkMiddle, Chunk.Radius)) continue;
                    shadowChunks.Add(renderData);
                }

                GraphicsDebug.PushGroup(_cascadeGroupNames[c]);
                ClientResources.ShadowFramebuffer.BindLayer(c);
                GL.Clear(ClearBufferMask.DepthBufferBit);
                GL.UniformMatrix4(uLightViewProj, false, ref lightViewProj);

                foreach (var chunk in shadowChunks)
                {
                    var worldMat = Matrix4.CreateTranslation(chunk.Chunk.Position.X * Chunk.Size, chunk.Chunk.Position.Y * Chunk.Size,
                        chunk.Chunk.Position.Z * Chunk.Size);
                    GL.UniformMatrix4(uWorld, false, ref worldMat);
                    chunk.Draw();
                }
                GraphicsDebug.PopGroup();
            }

            GL.Disable(EnableCap.PolygonOffsetFill);
            ClientResources.ShadowFramebuffer.Unbind(ClientResources.Window.FramebufferSize.X, ClientResources.Window.FramebufferSize.Y);
            GraphicsDebug.PopGroup();
        }

        /// <summary>Copies a Matrix4 into a flat float[] at <paramref name="offset"/> in OpenTK's native
        /// row-major byte order, so the mat4[] uniform upload (transpose=false) matches the single ref
        /// Matrix4 marshalling element-for-element.</summary>
        private static void FlattenMatrix(Matrix4 m, float[] dst, int offset)
        {
            dst[offset + 0] = m.Row0.X; dst[offset + 1] = m.Row0.Y; dst[offset + 2] = m.Row0.Z; dst[offset + 3] = m.Row0.W;
            dst[offset + 4] = m.Row1.X; dst[offset + 5] = m.Row1.Y; dst[offset + 6] = m.Row1.Z; dst[offset + 7] = m.Row1.W;
            dst[offset + 8] = m.Row2.X; dst[offset + 9] = m.Row2.Y; dst[offset + 10] = m.Row2.Z; dst[offset + 11] = m.Row2.W;
            dst[offset + 12] = m.Row3.X; dst[offset + 13] = m.Row3.Y; dst[offset + 14] = m.Row3.Z; dst[offset + 15] = m.Row3.W;
        }

        /// <summary>Fills the opaque/transparent draw lists for this frame and flags whether the sun shadow
        /// passes are needed (<see cref="_anyShadowReceiver"/>). Runs the camera visibility BFS, or — with
        /// occlusion culling disabled (F8) — the old linear all-chunks scan.</summary>
        private static void BuildVisibleSet(WorldClient world, Camera camera, Frustum viewFrustum)
        {
            _chunksToDraw.Clear();
            _transparentSortedChunks.Clear();
            _transparentChunks.Clear();
            _anyShadowReceiver = false;

            if (RenderDebug.DisableOcclusionCulling) BuildVisibleSetLinear(world, camera, viewFrustum);
            else BuildVisibleSetBfs(world, camera, viewFrustum);

            // Sort the near transparent chunks back-to-front, then build the final opaque draw list as
            // opaque + all transparent (so a transparent chunk's opaque faces are drawn in the opaque pass).
            _sortCameraPos = camera.Position;
            _transparentSortedChunks.Sort(_transparentSort);
            _transparentChunks.AddRange(_transparentSortedChunks);
            _chunksToDraw.AddRange(_transparentChunks);

            RenderDebug.DrawnChunks = _chunksToDraw.Count;
            RenderDebug.VisitedChunks = RenderDebug.DisableOcclusionCulling ? world.RenderList.Count : _bfsVisited.Count;
        }

        /// <summary>Buckets one visible chunk into the opaque/near-transparent/far-transparent draw lists
        /// (shared by both visibility paths) and flags it as a shadow receiver if it is sky-exposed and
        /// within the shadow distance.</summary>
        private static void Classify(ChunkRenderData renderData, float lengthSq)
        {
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

            if (renderData.SkyExposed && lengthSq <= ShadowDistanceSq) _anyShadowReceiver = true;
        }

        /// <summary>Occlusion culling: a BFS from the camera chunk that cannot cross a solid chunk (its
        /// <see cref="ChunkRenderData.Connectivity"/> graph gates which exit faces are reachable from the
        /// face the BFS entered through), so geometry behind rock is never queued. Visited is keyed on
        /// position only and there is no accumulated direction mask, so the reached set is a strict superset
        /// of the visible set — it can over-draw (e.g. around an L-bend) but never hides visible geometry.
        /// Traversal is frustum- and render-distance-culled (the camera's own chunk is exempt from the frustum
        /// test so the root never dies), which bounds the flood to the view cone. That is still correct for
        /// genuinely-visible chunks — see the convexity argument inline. BFS order is ~front-to-back.</summary>
        private static void BuildVisibleSetBfs(WorldClient world, Camera camera, Frustum viewFrustum)
        {
            var visited = _bfsVisited;
            var queue = _bfsQueue;
            visited.Clear();
            queue.Clear();

            var cameraChunk = WorldBase.ChunkInWorld(camera.Position.ToVector3i());
            visited.Add(cameraChunk);
            queue.Enqueue((cameraChunk, -1));

            var renderData = world.RenderData;
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                var pos = node.Pos;
                var entryFace = node.EntryFace;

                var center = (pos * Chunk.Size + new Vector3i(Chunk.Size / 2)).ToVector3();
                var lengthSq = (camera.Position - center).LengthSquared;
                if (lengthSq > RenderDistanceSq) continue;

                // Frustum-cull traversal — but never the camera's own chunk (entryFace < 0), whose centre can
                // sit behind the near plane (sphere fully outside) so culling the root would kill the BFS (black
                // screen). This is cheap (the flood is bounded to the view cone, not the whole render-distance
                // sphere) AND correct for genuinely-visible chunks: the frustum is convex with the camera at its
                // apex, so a straight unobstructed sight-line to any visible chunk stays inside the frustum, and
                // every chunk it crosses both passes the (conservative bounding-sphere) frustum test and connects
                // entry-face to exit-face through the air the ray travels — so the BFS reaches it. (Frustum-gating
                // can only drop chunks reachable solely via a bent, occluded path, which are not visible anyway.)
                if (entryFace >= 0 && !viewFrustum.SpehereIntersection(center, Chunk.Radius)) continue;

                int conn;
                if (renderData.TryGetValue(pos, out var rd) && rd.Uploaded)
                {
                    Classify(rd, lengthSq);
                    // The camera's own chunk lets the BFS exit any face (entryFace < 0); otherwise the
                    // chunk's connectivity graph decides which faces a sight-line entering this face reaches.
                    conn = entryFace < 0 ? ChunkRenderData.AllConnected : rd.Connectivity;
                }
                else
                {
                    // Missing (air / not-yet-streamed) or loaded-but-not-yet-meshed: passthrough, so an
                    // unstreamed gap never breaks connectivity and a missing chunk simply draws nothing.
                    conn = ChunkRenderData.AllConnected;
                }

                for (var f = 0; f < 6; f++)
                {
                    if (entryFace >= 0)
                    {
                        if (f == (entryFace ^ 1)) continue;
                        if ((conn & ChunkRenderData.PairBit(entryFace, f)) == 0) continue;
                    }

                    var np = pos + _bfsOffsets[f];
                    if (!visited.Add(np)) continue;
                    queue.Enqueue((np, f ^ 1));
                }
            }
        }

        /// <summary>Occlusion culling disabled (F8): the old linear scan of every loaded chunk, frustum +
        /// render-distance culled. Forces the shadow passes on whenever the sun is up, matching the
        /// pre-occlusion behaviour for A/B comparison.</summary>
        private static void BuildVisibleSetLinear(WorldClient world, Camera camera, Frustum viewFrustum)
        {
            var renderList = world.RenderList;
            for (var i = 0; i < renderList.Count; i++)
            {
                var renderData = renderList[i];
                var chunkMiddle = (renderData.Chunk.Position * Chunk.Size + new Vector3i(Chunk.Size / 2)).ToVector3();
                if (!viewFrustum.SpehereIntersection(chunkMiddle, Chunk.Radius)) continue;
                var lengthSq = (camera.Position - chunkMiddle).LengthSquared;
                if (lengthSq > RenderDistanceSq) continue;
                Classify(renderData, lengthSq);
            }

            _anyShadowReceiver = true;
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
            var uWorld = shader.GetUniformLocation("uWorld");
            var uCutoff = shader.GetUniformLocation("uCutoff");
            GL.UniformMatrix4(shader.GetUniformLocation("uView"), false, ref camera.View);
            GL.UniformMatrix4(shader.GetUniformLocation("uProjection"), false, ref projection);
            GL.Uniform1(uCutoff, 1);
            GL.Uniform1(shader.GetUniformLocation("uTextures16"), 0);
            GL.Uniform1(shader.GetUniformLocation("uTextures64"), 1);
            GL.Uniform1(shader.GetUniformLocation("uTextures256"), 2);
            GL.Uniform1(shader.GetUniformLocation("uTextures1024"), 3);

            BlockTextureManager.Bind();
            Samplers.BindBlockTextureSampler();

            //Draw opaque blocks front to back
            GraphicsDebug.PushGroup("Opaque");
            foreach (var chunk in _chunksToDraw)
            {
                var worldMat = Matrix4.CreateTranslation(chunk.Chunk.Position.X * Chunk.Size, chunk.Chunk.Position.Y * Chunk.Size,
                    chunk.Chunk.Position.Z * Chunk.Size);
                GL.UniformMatrix4(uWorld, false, ref worldMat);
                chunk.Draw();
            }
            GraphicsDebug.PopGroup();

            RenderState.Set(new GlState
            {
                CullFace = true, DepthTest = true, DepthFunc = DepthFunction.Lequal,
                Blend = true, BlendFunc = (BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            });
            GL.Uniform1(uCutoff, 0);

            //Draw transparent blocks back to front
            GraphicsDebug.PushGroup("Transparent");
            foreach (var chunk in _transparentChunks)
            {
                var worldMat = Matrix4.CreateTranslation(chunk.Chunk.Position.X * Chunk.Size, chunk.Chunk.Position.Y * Chunk.Size,
                    chunk.Chunk.Position.Z * Chunk.Size);
                GL.UniformMatrix4(uWorld, false, ref worldMat);
                chunk.DrawTransparent();
            }
            GraphicsDebug.PopGroup();

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

        /// <summary>Resolves the cascaded sun shadow at HALF resolution into <see cref="ClientResources.
        /// ShadowResolveFramebuffer"/>: a fullscreen quad runs the 12-tap cascaded PCF (moved out of the
        /// composition pass) per half-res pixel, writing the shadow factor + normalized depth. Composition then
        /// depth-aware-upsamples it. This trades the full-res PCF (~45% of the GPU frame) for a quarter-count
        /// PCF + a cheap upsample. Reads the G-buffer normal/depth/light + the cascade depth array.</summary>
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
            var cascadeCount = CascadeCount;
            GL.Uniform1(sh.GetUniformLocation("uCascadeCount"), cascadeCount);
            GL.UniformMatrix4(sh.GetUniformLocation("uLightViewProj"), cascadeCount, false, _lightViewProjFlat);
            GL.Uniform1(sh.GetUniformLocation("uCascadeSplits"), cascadeCount, _cascadeSplitsView);
            GL.Uniform1(sh.GetUniformLocation("uCascadeTexel"), cascadeCount, _cascadeTexelWorld);
            GL.Uniform1(sh.GetUniformLocation("uSunFade"), sunFade);
            GL.Uniform1(sh.GetUniformLocation("uShadowSoftness"), ShadowSoftness);
            GL.Uniform1(sh.GetUniformLocation("uShadowsEnabled"), GraphicsSettings.Shadows ? 1f : 0f);
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
            GL.Uniform1(comp.GetUniformLocation("uShadowMaxDepth"), _cascadeSplitsView[CascadeCount - 1]);
            GL.UniformMatrix4(comp.GetUniformLocation("uViewProjectionInv"), false, ref viewProjectionInv);
            GL.UniformMatrix4(comp.GetUniformLocation("uView"), false, ref camera.View);
            GL.Uniform1(comp.GetUniformLocation("uSunFade"), sunFade);
            GL.Uniform1(comp.GetUniformLocation("uShadowStrength"), ShadowStrength);
            // When shadows are disabled the resolve pass is skipped, leaving a stale buffer bound; this tells
            // the shader to treat every sky-lit surface as fully lit instead of sampling that stale buffer.
            GL.Uniform1(comp.GetUniformLocation("uShadowsEnabled"), GraphicsSettings.Shadows ? 1f : 0f);
            GL.Uniform1(comp.GetUniformLocation("uDebugShadow"), RenderDebug.ShadowFactor ? 1f : 0f);
            GL.Uniform1(comp.GetUniformLocation("uDebugCascade"), RenderDebug.CascadeTint ? 1f : 0f);
            var sun = SunColor();
            GL.Uniform3(comp.GetUniformLocation("uSunColor"), sun.X, sun.Y, sun.Z);
            var skyAmbient = SkyAmbient();
            GL.Uniform3(comp.GetUniformLocation("uSkyAmbient"), skyAmbient.X, skyAmbient.Y, skyAmbient.Z);
            ClientResources.ScreenRectVao.Draw();
            GraphicsDebug.PopGroup();

            //GL.Disable(EnableCap.Blend);
        }
    }
}
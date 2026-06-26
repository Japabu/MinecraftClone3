# Rendering pipeline (deferred)

```
  WorldRenderer.RenderWorld(WorldClient, projection)
    └─ BuildVisibleSet → frustum + render-distance scan of every loaded chunk; fills the
         opaque/transparent draw lists and flags _anyShadowReceiver (a visible sky-exposed chunk)
    └─ DrawShadowMap (only while the sun is up AND _anyShadowReceiver) → ShadowFramebuffer (depth-only)
         one depth map (Texture2D); opaque chunks re-drawn from the sun's orthographic POV (ShadowDepth shader)
    └─ DrawGeometryFramebuffer → GeometryFramebuffer (MRT G-buffer)
         attachment 0: diffuse   1: normal (w=1 ⇒ "unlit, pass through")
         2: RGBA8 light (rgb = baked block light, a = baked sky-light factor 0..1)   + depth
         · opaque chunks front-to-back, then transparent back-to-front (per-chunk sorted VAO)
         · EntityRenderer  : remote players as solid placeholder cubes (BlockOutline shader, unlit)
         · PlayerController : block-targeting outline
    └─ DrawShadowResolve (same gate as DrawShadowMap) → ShadowResolveFramebuffer (HALF-res RGBA8)
         the 12-tap PCF runs here at half res: r = shadow factor, g = norm view depth;
         reads G-buffer normal/depth/light + the shadow depth map
    └─ DrawComposition → screen
         background pixels (cleared far plane, viewDepth ≥ uSkyDistance) ⇒ SkyColor(viewRay) — the skybox;
         shadow = depth-aware (joint-bilateral) upsample of the half-res resolve buffer (1=lit, early-outed
         past ShadowDistance / in caves / at night); skyLight = sky.a*(shadow*uSunColor*uSunFade + uSkyAmbient);
         light = max(blockLight.rgb, skyLight); lit = diffuse * max(light, MinLight); water reflects SkyColor;
         lit fades into uHorizonColor with distance fog; normal.w==1 ⇒ diffuse unlit
```

Shaders live in `MinecraftClone3/Content/System/Assets/System/Shaders/`. (Separate from the deferred path
below, the `ItemIcon` shader forward-renders a single block into an off-screen inventory icon — fixed
per-face shading, no G-buffer; see [inventory.md](inventory.md).) Lighting is block-emitted RGB light
(torches) **plus sky light** modulated by a **dynamic day/night cycle**: the per-vertex light is a `vec4`
(rgb = smooth-lit block brightness, a = smooth-lit sky-occlusion factor); the composition multiplies the sky
factor by the **sun term** (`shadow * uSunColor * uSunFade`, `WorldRenderer.SunColor` — warm white at noon,
orange at the horizon, faded out at night) **plus an ambient sky term** (`uSkyAmbient`,
`WorldRenderer.SkyAmbient` — soft blue fill by day, dim cool **moonlight** at night). Because **both** scale
by the baked sky factor, sky-occluded **caves get no ambient or moonlight and stay dark** unless a block
light reaches them; only a tiny `MinLight` floor keeps unlit surfaces from being a literal void. The whole
thing animates **with no remesh** — the sky channel is baked into the chunk mesh (geometry/occlusion static),
only the sun/moon colour/intensity animate. The clock is **server-authoritative**: `WorldRenderer` reads
`WorldClient.WorldTimeSeconds` (synced from the periodic `WorldTime` packet, advanced locally between
packets), so all MP clients share one time of day. Moonlight is non-directional ambient (no moon shadow
pass) — deferred.

**Background sky (the skybox) — procedural, in `Composition.fs`, no geometry.** Background pixels are the
*cleared far plane*: `main()` reconstructs each pixel's view-space depth and, when it is `≥ uSkyDistance`
(`RenderDistance + 48`, past the farthest drawn chunk but inside the 512 far clip), reuses the far-plane
point as the view-ray and shades `SkyColor(dir)` — so the sky costs nothing but the fullscreen pass that
already runs. `SkyColor` builds a Minecraft-style sky from `WorldRenderer` time-of-day uniforms: a vertical
gradient (`uVoidColor` → `uHorizonColor` haze → `uSkyColor` zenith), a sunrise/sunset orange band near the
horizon in the sun's azimuth (`uSunsetColor`), procedural **stars** (hashed direction grid, faded in by
`uStarBrightness` at night, out toward the horizon), and a **textured sun and moon** drawn as angular
billboards (`CelestialBillboard` projects the view ray onto a tangent plane → quad uv). The sun/moon textures
are the real pack assets (`minecraft/textures/environment/sun.png`, `moon_phases.png` full-moon cell), loaded
by `WorldRenderer.LoadSkyTextures()` onto composition units 5/6; with **no pack** the
`uHasSunTexture`/`uHasMoonTexture` flags are 0 and the shader falls back to a procedural disc. The moon sits
opposite the sun (`-uSunDirection`), each hidden below its own horizon. The billboards bind a dedicated
**celestial sampler** (`Samplers.BindCelestialSampler`, nearest / no mipmaps / clamp-to-edge) — the small
on-screen disc would otherwise sample the auto-generated mipmaps through GL's default sampler 0 and read as a
dim blurred blob with edge bleed; nearest filtering keeps the pixel-art sun/moon crisp. **Water reflects this same `SkyColor`** (see the water section). **Distance fog** melts lit geometry into `uHorizonColor` between
`uFogStart`/`uFogEnd` (0.72–0.97 × `RenderDistance`), hiding the chunk-load boundary against the sky (and
fading to night darkness as the horizon colour dims). The sky/sun/star/fog colours are all `WorldRenderer` C#
functions (`SkyZenithColor`/`SkyHorizonColor`/`SkyVoidColor`/`SunsetColor`/`StarBrightness`, sharing
`DayTime`/`SunHeight`/`DayFactor` with `SunColor`/`SkyAmbient`/`SunDirection`), so retuning needs no shader
recompile; billboard sizes are the `SunSize`/`MoonSize` consts.

**Directional sun shadows — one low-res shadow map (no cascades).** `DrawShadowMap` renders a **single**
orthographic depth map into `ShadowFramebuffer` — one `Texture2D` of `ShadowFramebuffer.ShadowMapSize`
(1024). **The map is deliberately low-res for a soft, blurry look:** the PCF penumbra is a fixed number of
*texels*, so a coarse world-per-texel reads as a wide, soft world-space penumbra. Bump `ShadowMapSize` (and
the `ShadowTexel` constant in `ShadowResolve.fs`) for sharper shadows, or shorten `ShadowDistance` for finer
texels over less ground; low-res is an art-direction choice. (CSM was removed — a single map is sufficient
for a player-centred voxel world; cascades only buy crisp near-shadows the project doesn't want.) The map is
fit to the **analytic bounding sphere** of the `[ShadowNear, ShadowDistance]` (160) view-frustum slice: the
centre rides the camera forward axis and the **radius depends only on near/far + FOV, so it is constant as
the camera rotates → no size shimmer**. The projection is **deliberately NOT texel-snapped**: the sun
advances every frame, and snapping to the *rotating* light-space texel grid quantizes the shadow's smooth
crawl into ~20 Hz whole-texel jumps (visible flicker). Unsnapped, the projection follows the sun smoothly and
the soft low-res PCF keeps camera-motion shimmer down. (Texel snapping is best practice for a *static* sun; a
fast moving sun inverts the trade.) The sun direction comes from `WorldRenderer.SunDirection()`. The pass
re-draws the **already-uploaded opaque chunk VAOs** (no remesh) with the trivial `ShadowDepth` shader,
frustum-culling against the light frustum; `PolygonOffset` + a normal-offset (scaled by `_shadowTexelWorld`)
+ a small depth bias fight self-shadow acne (culling is **off** — the mesher emits only single-sided exposed
faces). The depth map is a **hardware shadow sampler** (`CompareRefToTexture` + `Linear`), so each PCF tap is
a free 2×2 bilinear comparison; the shader takes a **12-tap Poisson disc, rotated per pixel**
(interleaved-gradient-noise angle) of radius `uShadowSoftness` texels, giving a soft band-free penumbra
(raise it for softer shadows at no extra tap cost). **Two tunable look knobs** (uniforms from `WorldRenderer`
fields, no shader recompile): `ShadowSoftness` (penumbra radius, default 8) and `ShadowStrength` (0..1,
default 0.65) — the latter lifts the shadow floor via `litShadow = mix(1, shadow, uShadowStrength)` so a
fully-shadowed surface keeps some direct sun; it scales **only** the direct-sun term, so ambient fill and
caves are untouched.

**The PCF runs at HALF resolution (`DrawShadowResolve` → `ShadowResolve.fs`), not per full-res pixel,**
because PCF is fill-bound and our integrated GPU (UHD 630) is fill-limited — the full-res 12-tap was the
single biggest GPU pass. It runs in a dedicated fullscreen pass into a **half-res RGBA8 target**
(`ShadowResolveFramebuffer`, quarter the invocations): `r` = the 0..1 shadow factor, `g` = normalized view
depth (for the upsample). `ShadowResolve.fs` does world-pos reconstruction (`uViewProjectionInv`),
light-space projection (`uLightViewProj`), and a **10%-of-`ShadowDistance` far fade**. Composition then
**depth-aware (joint-bilateral) upsamples** the half-res buffer back to full res: a 2×2 tap weighted by
bilinear position **and** by how close each tap's stored depth (`g`) is to the pixel's
(`exp(-|Δdepth|·DepthSharpness)`), so the shadow doesn't bleed across silhouette edges (`DepthSharpness` in
`Composition.fs` is the edge-vs-blockiness knob). The result multiplies **only** the direct sun part of the
sky term — ambient sky fill (`uSkyAmbient`) and block light untouched, so a sky-exposed shadow falls back to
blue sky fill (not black), a sky-occluded one (a cave) goes dark. Both the resolve and the upsample are
**early-outed** where the sun term can't matter — past `ShadowDistance`, sky-occluded (`uLight.a ≈ 0`), or at
night/dusk (`uSunFade ≈ 0`) — which is the bulk of the fullscreen win (a wide view is mostly far/occluded
terrain). The resolve shares `DrawShadowMap`'s gate; when skipped the half-res buffer is stale but
composition's identical early-outs never sample it.

**Dusk/night — the whole directional sun term fades, not just the shadows.** A naive "skip the shadow pass
below the horizon" toggles `shadow` to 1 at the cutoff while `uSunColor` is still bright orange, popping every
shadowed surface bright. Instead `WorldRenderer.SunFade(toSun.Y)` is a smoothstep over `[ShadowFadeLow,
ShadowFadeHigh]` (sun altitude) ramping a single `uSunFade` (0..1); the composition multiplies the **direct
sun term** (`sky.a * shadow * uSunColor * uSunFade`) by it. As the sun sets, sunlit surfaces dim *down to
meet* the shadowed ones and everything converges to the ambient sky term (itself crossfading to moonlight)
with no pop; `uSunFade = 0` exactly where the shadow pass cuts off, so it also gates shadow sampling. Block
light and the ambient sky term are never scaled by `uSunFade`, so torches and moonlight are unaffected.

**Visible set — a linear frustum + render-distance scan (no occlusion culling).** `BuildVisibleSet` iterates
`WorldClient.RenderList`, keeps each chunk whose bounding sphere is in the view frustum and within
`RenderDistance`, and buckets it into the opaque / near-transparent / far-transparent draw lists. The mesher
emits only air-exposed faces, so a buried chunk's mesh is near-empty and costs almost nothing to submit; the
dominant surface GPU cost is the shadow pass + composition fill, not buried-chunk draws. (A per-chunk
visibility-graph BFS "cave cull" was tried and removed — it over-drew on open vistas and didn't reliably win
in caves.)

**The visible set gates the shadow passes.** `BuildVisibleSet` sets `_anyShadowReceiver` iff a visible chunk
within `ShadowDistance` is **sky-exposed** (`ChunkRenderData.SkyExposed = Chunk.HasAnySkyLight()`), and the
shadow depth pass runs only when `sunUp && _anyShadowReceiver && GraphicsSettings.ShadowsEnabled` (the
**Shadows** quality option ≠ Off). Deep in a cave nothing visible is sky-lit, so the passes (a fixed
per-frame GPU cost) are skipped entirely; look toward the surface and they run again. The sun is a
*directional* viewer, so this decides only **whether to run the passes**, not prune casters — `DrawShadowMap`
keeps its light-frustum caster cull. When skipped the **stale** shadow map is left bound but never sampled,
because composition early-outs shadow sampling exactly where `_anyShadowReceiver` is false **or** where the
Shadows option is off (`uShadowsEnabled=0` forces `shadow=1`).

**Water surface — Tier B (animated normals + Fresnel sky reflection + sun specular, no extra pass).** Water
is shaded specially **in `Composition.fs`** — deferred-correct, since composition already reconstructs world
position from depth and holds the sun/sky/shadow terms. No new render pass, framebuffer, or vertex attribute.
The chain: `BlockWater.GetRenderMaterial → RenderMaterial.Water` (an engine-level hint mirroring
`TransparencyType`); `ChunkMesher.AddFaceToVao` bakes that into the face **`normal.w = 0.5f`**
(`WaterNormalW`), which `EncodeNormal` stores as **0.75** (≈191/255) in the G-buffer normal alpha — distinct
from lit solid (0.5) and the unlit flag (1.0). Composition detects a water pixel by a snug band around it
(`WaterFlagLo/Hi`, 0.7–0.8 — the flag is flat per-face and attachment 1 is written blend-off, so the stored
value is deterministic; a future `RenderMaterial` must encode outside this band) and, on top of the existing
Tier-A lit translucent water (`baseColor`), adds: animated **`WaveNormal`** (analytic gradient of three
summed directional sine waves over the surface's world XZ, scrolled by `uTime` — only the **top** face,
`faceN.y > 0.5`, is perturbed), a **Fresnel** mix toward **`SkyColor(reflect(-V, N))`** (the *same* sky
function the skybox paints, so the water mirrors the real gradient, sun, moon, stars and tracks time of day),
and a **Blinn-Phong sun specular** glint plus a matching **moon specular** glint. New composition uniforms:
`uCameraPos`, `uSunDirection`, `uTime`, `uMoonFade`, `uMoonColor`. The sun glint is scaled by `uSunFade` (and
`shadow`), so it fades out at dusk; the moon glint (half-vector toward `-uSunDirection`, cool `uMoonColor`)
fades *in* by `uMoonFade` (`SunFade(-sunY)`) so the moon reflects on night water once the sun's glint is gone.
Both, and the Fresnel reflection, are scaled by the baked sky factor (`uLight.a`), so cave/overhang water falls
back to the plain look for free. Look knobs are shader `const`s (wave amp/freq/speed, `WaterF0`,
`WaterSpecExp/Gain`).

The one subtlety: composition must *identify* water pixels, but the transparent pass blends **all three** MRT
attachments, so a flag in `normal.w` would blend with the background and become unreadable. Fix: during the
transparent draw, **blend only attachment 0 (diffuse)** and disable blending on attachments **1 (normal)**
and **2 (light)** via `GL.Disable(IndexedEnableCap.Blend, 1/2)`, restored to `Enable` right after the pass.
Diffuse still alpha-blends (translucency preserved); the front-most transparent surface writes its normal +
water-flag + light **cleanly** (overwrite, not blend). This needs **no `RenderState` change** — the explicit
restore keeps RenderState's single `Blend` bool the whole description. Side effect (intentional): glass also
writes its front pane's own normal/light instead of blending them, removing a latent `normal.w` corruption
when glass overlapped an unlit pixel. `WorldGeometry.vs/.fs` are untouched.

OpenGL is capped at **4.1 Core / GLSL 4.10** (macOS limit). Consequences: **uniform and sampler locations are
queried by name** (no `layout(location=)`/`layout(binding=)` on uniforms); vertex-attribute and
fragment-output locations *do* use `layout(location=)`.

**macOS VAO element-binding quirk.** On the macOS OpenGL-over-Metal driver, deleting *unrelated* VAOs/buffers
resets the **element-array-buffer binding of other, live VAOs** to 0. A `glDrawElements` on such a VAO then
raises `GL_INVALID_OPERATION`, draws nothing, and (because the error wedges the command stream) stops the
window surface from presenting — a frozen/black window even though the app loop is alive. This bit the
persistent `ScreenRectVao` (every GUI draw + the fullscreen passes) after a world teardown disposed the chunk
arenas, and could silently drop transparent chunk meshes during eviction. Fix: `SpriteVertexArrayObject.Draw`
and `VertexArrayObject.Draw` **re-bind their element buffer every draw** (`GL.BindBuffer(ElementArrayBuffer,
IndicesId)` after `BindVertexArray`) instead of trusting the VAO to retain it. Don't remove that line.

## Phase-2 distant horizon (LOD)

**Full detail to the render distance — the only LOD is the Phase-2 horizon beyond it.** Loaded chunks
(≤ `ServerNetwork.ViewDistance`) always mesh at full per-block detail (`ChunkRenderData.AddBlocksToVao`); a
within-RD heightmap LOD that coarsened near chunks looked bad and is not done. Coarse terrain only appears
*past* the render distance, where it is far and fog-hidden — a second streaming channel of surface-only
columns that lets terrain recede to a fogged horizon `LodRingChunks` (= `LodHorizonChunks`, default 64, max
96) chunks past the render distance, the Distant-Horizons look. RD 16 + a 64-chunk horizon runs ~180 uncapped FPS on the
dedicated GPU; the horizon is nearly free (far rings are 4×/16× cheaper + fog-occluded). The data model
(`LodColumn`/`LodColumnStore`, stride-2 packed columns), server-side LOD generation/decoration, the
`LodColumnData` packet, and the split-priority mesh worker that keeps the horizon from starving live elsewhere
— see [world-model.md](world-model.md), [worldgen.md](worldgen.md), [networking.md](networking.md),
[threading.md](threading.md). The rendering-side pipeline:

**Client render data + mesh.** On the main thread `WorldClient.DrainLodRenderReady` creates a GL-free
`LodRenderData` per region + a `LodRenderList` entry; the **shared mesh pool** meshes it (`TryMeshOneLod` →
`ChunkMesher.AddLodColumnRegionToVao`), and the main-thread upload loop uploads it into a **second
`ChunkMeshArena` (`LodArena`)** after the chunk uploads, inside the same frame budget. `EvictDistantLod` drops
regions past `LodCacheDistance`. All GL is main-thread (Invariant 1).

**The run-list mesher (`ChunkMesher.AddLodColumnRegionToVao(store, key, vao, meshStep)`).** Same flat-top +
skirt + `IsLodSurface` + skirt-shade emission as the within-RD mesher, but driven by the packed columns (no
world, no real blocks): `GameRegistry.BlockRegistry[blockId]` for the block, world-free
`GetTintColor`/`GetRenderMaterial` (grass/water tint by position, water reflection flag), sky-only light. Tops
are **greedy-merged** (runs of identical columns → one quad — water/plains collapse, the big geometry win);
**skirts stay per-cell**. `meshStep` is the DH detail ring: the store is **stride-2**, so meshStep 1/2/4/8 =
stride 2/4/8/16. The mesher downsamples the stride-2 store to a super-grid by **MAX surface** (`MaxSurfaceCell`
— keeps trees/peaks from sinking, so forests stay canopy plateaus when coarsened), computing neighbour
super-cells on demand (`SuperNeighbourY`). `LodRenderData.DesiredMeshStep` is set by
`WorldClient.ScanLodForMeshStep` (own chunk-cross gate) from the region's **horizontal (XZ) distance**
(`MeshStepFor` — XZ not 3D, matching the horizontal annulus + `EvictDistantLod`, so altitude doesn't coarsen
the horizon): **stride-2 for the first `LodRing1Distance` (16 chunks) past the render distance** (the nearest,
most-visible horizon stays fine — a gentle step down from full detail), then stride-4/8/16, with **rings ≥ 1
region (8 chunks) wide so adjacent regions never differ by >1 step (no >2× crack)**, scaled by the **LOD
Quality** option. The loopback apply path **shares the server's immutable region by reference** (no per-region
clone).

**Render integration (`WorldRenderer`).** `BuildLodVisibleSet` frustum + distance-culls `LodRenderList` to
`[RenderDistance − FadeBandWidth, LodRenderDistance]` (inner edge pulled in for the cross-fade band). The
geometry pass draws `LodArena` **after** the real chunks with **`DepthFunc.Less`** (already-drawn nearer real
chunks win → the inner overlap is hidden, DH overdraw-prevention), same G-buffer + shader. Because drawn
geometry now reaches past the render distance, the **projection far plane** (`StateWorld`, near plane raised to
0.1 for precision) and the composition **`uSkyDistance` + fog band** are widened to the LOD horizon, so the fog
melts the coarse far ring into `uHorizonColor` before the sky. **No LOD in the shadow pass.**

**Dithered cross-fade (LOD morph, no pop).** Full-detail chunks dissolve into the horizon over a 32-block band
(`FadeBandWidth`) just inside the render distance instead of popping when a chunk loads/unloads at the edge.
`WorldGeometry.vs` passes `vWorldPos`; the `.fs` does a complementary **Bayer-4×4 dither discard** (chunk fades
OUT where `dither < fade`, LOD fades IN where `dither >= fade`, `fade` = camera distance across the band) so
**exactly one of {chunk, horizon} survives per pixel** — no gaps, no double-draw, no z-fight. `WorldRenderer`
sets `uFadeMode` 0 (chunk) / 1 (LOD) per batch + the band uniforms; disabled (start past the far plane) when
the horizon is dormant. The server LOD inner overlap (`LodInnerOverlap`) is widened so the store covers the
band with no holes to fade into.

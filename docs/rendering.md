# Rendering pipeline (GPU-driven deferred)

The renderer is a deferred, GPU-driven pipeline on **WebGPU** (Silk.NET.WebGPU → wgpu-native → Metal on macOS,
Vulkan on Linux, D3D12 on Windows). A compute shader frustum/distance-culls chunks into an indirect-multidraw
buffer; geometry composes into a 3-MRT G-buffer with **reverse-Z** depth and an **HDR** scene target; a final
tonemap pass maps to the surface.

```
  WorldRenderer.RenderWorld(WorldClient, projection)            // one frame, recorded into Renderer.Encoder
    └─ ScanTransparentAndShadow  → CPU back-to-front transparent list + _anyShadowReceiver flag
                                    (opaque/LOD/shadow visibility is decided on the GPU; no CPU opaque list)
    └─ cull dispatches (compute, BEFORE the render pass — compute can't nest in a render pass):
         shadowCull (light frustum) · opaqueCull (view frustum + RD) · lodCull (view frustum + LOD distance)
                                    each writes its own DrawIndexedIndirect list + atomic count
    └─ DrawShadowMap (only while the sun is up AND _anyShadowReceiver AND Shadows≠Off)
         depth32f shadow map; opaque arena re-drawn from the sun's reverse-Z ortho POV via the shadow indirect list
    └─ geometry pass → GBufferTargets (3 MRT + depth32f)
         0 diffuse RGBA8   1 normal RGBA8 (.w material flag)   2 light RGBA8 (rgb block light, a sky factor)   + depth32f
         · opaque arena (indirect multidraw), then LOD horizon arena (indirect, dithered cross-fade),
           then CPU-sorted transparent INCLUDING water into the SAME G-buffer
         · overlays into the same pass: EntityRenderer, BlockEntityRenderer, PlayerController (outline/break),
           ChunkBorderRenderer, HeldItemRenderer (first-person viewmodel, last)
    └─ DrawShadowResolve (same gate as DrawShadowMap) → half-res RGBA8
         12-tap rotated-Poisson PCF (comparison sampler, Greater): r = shadow factor, g = normalized view depth
    └─ DrawComposition → HDR scene (rgba16float)
         background pixels (reverse-Z far clear, depth==0) ⇒ SkyColor(viewRay); shadow = joint-bilateral upsample
         of the half-res resolve; skyLight = sky.a·(shadow·SunColor·SunFade + SkyAmbient); light = max(blockLight, skyLight);
         lit = diffuse · max(light, MinLight); water reflects SkyColor; distance fog melts into HorizonColor
    └─ MarkSceneRendered  → Renderer.EndFrame tonemaps the HDR scene to the surface, flushes GUI over it, presents
```

Shaders are **WGSL** in `MinecraftClone3/Content/System/Assets/System/Shaders/`. (Separate from the deferred
path, the `ItemIcon` shader forward-renders a single block into an off-screen inventory icon — fixed per-face
shading, no G-buffer; see [inventory.md](inventory.md).)

## RHI layer and the `Gpu` facade

All `unsafe` Silk.NET.WebGPU interop is confined to `MinecraftClone3API.Client/Graphics/Rhi/`. `GpuContext`
owns the instance/adapter/device/queue/surface and is built once from the Silk.NET window; everything else
reaches the device through the static **`Gpu`** facade (`Gpu.Device`/`Queue`/`Api`/`Features`/`SurfaceFormat`),
the accepted GL-style global. The wrappers are `Gpu`-prefixed: `GpuBuffer`, `GpuTexture`, `GpuSampler`,
`GpuShaderModule`, `Gpu{Render,Compute}Pipeline`, `GpuBindGroup(Layout)`, `GpuPipelineLayout`, plus
`FrameContext`, `RenderPassBuilder`/`ColorAttachment`/`DepthAttachment`, `ComputePass`, `MatrixConvert`, and
`Projection`. `Renderer` is the per-frame conductor: `BeginFrame` acquires the swapchain image and opens the
frame command encoder, states record draws into it, `EndFrame` opens the surface pass and tonemaps + flushes
the GUI. The surface format is chosen **non-sRGB UNORM** (the renderer tonemaps in display space and would
otherwise double-encode gamma), and the swapchain is configured **`CompositeAlphaMode.Opaque`** so the OS
compositor ignores surface alpha — a GUI blend that writes alpha 0 (the crosshair's inverting blend) must not
punch a transparent hole, so that blend also leaves destination alpha untouched.

`GpuFeatures` detects the wgpu-native optional features each modern path gates on, with a graceful fallback for
every one: **`MultiDrawIndirectCount`** (else a per-slot indirect-draw loop), **`PushConstants`** (else
dynamic-offset UBOs — note the device must be created with a non-zero `maxPushConstantSize` chained limit, not
just the feature enabled), and **`TimestampQuery`** (the F3 `gpuMs` per-pass timing). The present mode follows
the vsync setting (`Fifo`/`Mailbox`/`Immediate`, falling back to `Fifo`).

## The G-buffer and reverse-Z

`GBufferTargets` is three colour attachments + a **depth32float** depth target, all also `TextureBinding` so
later passes sample them:

| Attachment | Format | Contents |
|---|---|---|
| 0 diffuse | RGBA8Unorm | albedo · vertex tint; a = coverage |
| 1 normal | RGBA8Unorm | encoded axis normal `n·0.5+0.5`; **.w material flag** (0.5 lit, 0.75 water, 1.0 unlit-passthrough) |
| 2 light | RGBA8Unorm | rgb baked block light, **a baked sky-light factor** (0..1) |

**Reverse-Z everywhere.** `Projection.ReverseZPerspective` maps near→1 and far(∞)→0 (an *infinite* far plane —
no far clip to z-fight against); the depth buffer is **cleared to 0** and the geometry pipelines compare
**`GreaterEqual`** (so a coplanar overlay layer — e.g. a tinted grass-side over its base — still draws; the
shadow pass uses strict `Greater`). This spreads floating-point depth precision evenly across the view distance
instead of bunching it at the near plane — which is what lets the **distant LOD horizon** avoid the z-fighting
conventional depth suffers there. `Projection` also provides a finite variant and `ReverseZOrtho` for the sun
shadow map.

## HDR + tonemap

The composition pass writes into an offscreen **`Renderer.HdrScene`** (rgba16float, also `CopySrc` so the
screenshot path can read it back). `EndFrame` then runs `Tonemap.wgsl` over a vertex-less fullscreen triangle
into the surface. Lighting is composited **directly in display (gamma) space**, so tonemap is just a
`clamp([0,1])` — no curve, no gamma re-encode — and the GUI sprite batch flushes over the top before present.
The present pass also carries the **nether-portal screen warp**: a `Renderer.PortalWarp` (0..1) push constant
displaces the sample uv with crossed sine waves (Minecraft's nausea wobble) while a player soaks in a portal;
0 leaves the pass byte-identical, so it only ever touches the frame when you're mid-transfer.

## GPU-driven chunk culling

Opaque chunks, LOD regions, and shadow casters are culled **on the GPU**; the CPU never builds those visible
sets. `ChunkMeshArena` is a shared vertex/index arena: every chunk's opaque mesh occupies a sub-range of one
big buffer set (a coalescing first-fit `RangeAllocator`; buffers grow via a transient encoder), and the arena
publishes a per-slot **`ChunkMeta`** storage buffer (world-space AABB min corner + `indexCount`/`firstIndex`/
`baseVertex`; `indexCount==0` marks an empty/freed slot). Positions are baked world-space at mesh time, so
there is no per-chunk model matrix and the whole set shares one draw.

Each frame, one `ChunkCuller` per pass uploads its frustum planes + camera distance and dispatches
`Cull.compute.wgsl` (one invocation per resident slot): a conservative AABB-vs-frustum positive-vertex test
plus an optional centre-distance cull (the client caches chunks past the render distance; `maxDistance ≤ 0`
disables it — the shadow pass relies on its own light frustum). Visible slots emit a `DrawIndexedIndirect`
command. Two output modes, picked by `Gpu.Features.MultiDrawIndirectCount`:

- **Compact (Vulkan/D3D12):** atomically append visible commands into a packed list + count; the pass issues
  one **`MultiDrawIndexedIndirectCount`**.
- **Per-slot (Metal — wgpu lacks `MultiDrawIndirectCount` there):** write one command per slot (culled/empty
  slots are zero-command no-ops); the pass loops plain `DrawIndexedIndirect` over the CPU-known resident count.

Applies to the **opaque** geometry, the **shadow-depth** pass (same arena, position-only vertex stream, light
frustum), and the **LOD** horizon (its own `LodArena`). Only **transparent** chunks stay CPU-sorted —
`ScanTransparentAndShadow` gathers them back-to-front (the GPU can't sort alpha) and that same scan flags
`_anyShadowReceiver`.

## Bind-group convention + push constants

Bind groups are organized by update frequency:

- **group 0 — per-frame** `FrameUniform` (view, proj, cameraPos), owned by `Renderer` and written once per
  frame (`GpuLayouts.Frame`), shared by every world pass.
- **group 1 — per-pass / per-draw**: the geometry `GeoParams` UBO (LOD cross-fade band + cutout flag, three
  flavours: opaque / LOD / transparent), the shadow/resolve/composition param UBOs, or a dynamic-offset UBO
  for entities.
- **group 2 — textures + samplers**: `GpuLayouts.BlockAtlas` (the four atlas arrays + the block sampler), or
  a pass's G-buffer/sky texture set.

Per-draw GUI sprites, the block outline, and the block-break overlay use **push constants** when available
(their shaders carry no Frame bind group — `Renderer.View`/`Projection` are cached so they can bake a full MVP
into the push constant); the fallback is dynamic-offset UBOs. Because WGSL uniforms follow std140 (`vec3`+`f32`
packs to 16 B), every C# UBO struct mirrors its WGSL header byte-for-byte (the `CompositionParams` /
`ShadowResolveParams` layouts are documented in their shaders).

**Matrices** upload row-major straight into WGSL's column-major: that raw byte copy **is** the transpose
WebGPU needs, so `MatrixConvert.ToGpu` does no explicit transpose. (`Projection` builds in a row-vector
convention to compose with the camera math; the WGSL read flips it back.)

## The block atlas and packed vertex

The atlas is **four size-bucketed `texture_2d_array`** (16/64/256/1024 px), bound together (`EnsureAtlasBind`);
the shader selects by the per-vertex `arrayId`. WebGPU can't put anisotropy and nearest-magnification on one
sampler (aniso requires all-linear filters), so the atlas binds **two** samplers and `sampleAtlas` picks per
fragment: **nearest** while magnifying (crisp pixel-art, texel ≥ pixel), **all-linear + 16× anisotropy** once
minifying, so grazing/distant surfaces stay sharp instead of over-blurring to a coarse mip. Blocks use **Repeat**
wrap (faces tile); **entities/worn armor** sample the same arrays through a parallel **clamp** sampler
(`GpuSamplers.Entity`) since their unwraps don't tile and armor sheets are transparent-padded — clamp stops the
opposite edge bleeding in at distance. Mips are built on the **CPU** (`BlockMipChain`) and uploaded level by
level (WebGPU has no `GenerateMipmap`); the downsample itself is a plain 2×2 box. The one non-obvious step is
**hole dilation**: a cutout texture's fully-transparent holes carry black RGB, and the trilinear min filter
re-mixes that black into every minified leaf edge at *sample* time (the texel is alpha-0 and discarded, but its
colour still averages in) — darkening distant foliage. `BlockMipChain.Dilate` floods the surrounding colour
outward into the holes, on the base level and every mip, so the filter blends leaf-with-leaf; it's a no-op on
textures with no holes (opaque blocks, water).

**Animated blocks** (water/lava/fire/nether portal) are driven by `BlockAnimator` (`RenderWorld` calls it once
per frame). Each `.mcmeta` strip was sliced into one layer per frame at load; the animator simply re-uploads the
current frame's pixels into the layer block faces bake (frame 0's) at the strip's `frametime` cadence — so
animation costs one small `QueueWriteTexture` per flip with **no shader, mesher, or bind-group change**.

The packed chunk vertex is **32 bytes** in five parallel streams (`MeshBuffer` / `ChunkMeshArena`): pos
`float3`, uv `float2`, a `packed` u32 (`texId | arrayId<<16 | normalIdx<<18 | material<<21`), tint `unorm8x4`,
light `unorm8x4`. `WorldGeometry.wgsl`'s vertex shader unpacks it; the fragment shader writes the three MRTs
(`o.normal = n·0.5+0.5` with the material flag in `.w`). The shadow pipeline binds slot 0 (position) only.

## Directional sun shadows

**One low-res orthographic shadow map (no cascades).** `DrawShadowMap` renders opaque chunk depth from the
sun's reverse-Z ortho POV (via the shadow cull's indirect list) into a depth32float map. The map is fit each
frame to the **analytic bounding sphere** of the `[ShadowNear, ShadowDistance]` view-frustum slice: the centre
rides the camera forward axis and the **radius depends only on near/far + FOV, so it is constant as the camera
rotates → no size shimmer**. The projection is deliberately **NOT texel-snapped** (the sun moves every frame;
snapping to the rotating light-space grid would quantize its smooth crawl into visible whole-texel jumps). The
map is deliberately low-res for a soft, blurry look — the PCF penumbra is a fixed number of *texels*, so a
coarse world-per-texel reads as a wide soft penumbra. `ShadowDistance`/`ShadowMapSize` follow the Shadow
Quality preset (Low 96/512, Medium 160/1024, High 256/2048). The shadow pipeline culls `None` (the mesher
emits single-sided exposed faces) and uses `depthBias`/`depthBiasSlopeScale` against acne.

**The PCF runs at HALF resolution** (`DrawShadowResolve` → `ShadowResolve.wgsl`) to cut its fill cost. A
fullscreen triangle reconstructs world position from the camera depth
(`uViewProjectionInv`, reverse-Z), projects into light space (`uLightViewProj`), and runs a **12-tap Poisson
disc rotated per pixel** (interleaved-gradient-noise angle) against the map. The depth comparison uses a
**comparison sampler with `CompareFunction.Greater`** (a fragment is lit when its light-space depth exceeds the
stored occluder depth — the reverse-Z convention; the reference depth is biased *away* from the light, and the
far-plane early-out is `p.z ≤ 0`). It writes `r` = shadow factor, `g` = normalized view depth, with a 10%-of-
`ShadowDistance` far fade. Composition then **depth-aware (joint-bilateral) upsamples** the half-res buffer: a
2×2 tap weighted by bilinear position **and** by `exp(-|Δdepth|·DepthSharpness)` so the shadow doesn't bleed
across silhouettes. Both the resolve and the upsample are **early-outed** where the sun term can't matter — past
`ShadowDistance`, sky-occluded (`light.a ≈ 0`), shadows-off, or at night/dusk (`SunFade ≈ 0`).
**In-shader map border:** light-space samples that fall outside the map read as lit
("outside the coverage is sunlit").

Two tunable look knobs (uniforms, no recompile): `ShadowSoftness` (penumbra radius in texels, free) and
`ShadowStrength` (0..1; `litShadow = mix(1, shadow, strength)` lifts the shadow floor, scaling **only** the
direct-sun term so ambient/caves are untouched).

**The visible set gates the shadow passes.** `ScanTransparentAndShadow` sets `_anyShadowReceiver` iff a visible
chunk within `ShadowDistance` is sky-exposed (`ChunkRenderData.SkyExposed`); the shadow depth + resolve passes
run only when `sunUp && _anyShadowReceiver && ShadowsEnabled`. Deep in a cave nothing visible is sky-lit, so
the passes (a fixed per-frame cost) are skipped; composition early-outs shadow sampling exactly where they
didn't run (treating sky-lit surfaces as fully lit).

## Day/night lighting and the procedural sky

Lighting is block-emitted RGB light (torches) **plus sky light** modulated by a **dynamic day/night cycle**.
The per-vertex light `vec4` is baked into the mesh (rgb block brightness, a sky-occlusion factor), so the whole
cycle animates **with no remesh** — only the sun/moon colour and intensity change. `Composition.wgsl` builds
`skyLight = sky.a · (litShadow · SunColor · SunFade + SkyAmbient)`, then `light = max(blockLight.rgb, skyLight,
AmbientFloor)` and `lit = diffuse · max(light, MinLight)`. Because **both** sun and ambient terms scale by the
baked sky factor, **sky-occluded caves stay dark** unless a block light reaches them. The clock is
**server-authoritative** (`WorldClient.WorldTimeSeconds`, synced via the `WorldTime` packet, advanced locally),
so MP clients share one time of day; the benchmark can pin it (`FixedTimeOfDay`). Moonlight is non-directional
ambient (no moon shadow pass).

**Dusk/night fades the whole directional sun term, not just the shadows.** `SunFade(toSun.Y)` is a smoothstep
over sun altitude; the composition multiplies the **direct sun term** (`sky.a · shadow · SunColor · SunFade`)
by it, so as the sun sets sunlit surfaces dim down to meet shadowed ones and everything converges to the
ambient/moonlight term with no pop. `SunFade = 0` exactly where the shadow pass cuts off, so it also gates
shadow sampling. Block light and ambient are never scaled by it.

**The background sky is procedural, in `Composition.wgsl`, with no geometry.** Background pixels are the
reverse-Z far clear (`depth == 0`); under the infinite-far projection that point is at infinity, so the shader
reconstructs the view ray from two finite depths off `uViewProjectionInv` and shades `SkyColor(dir)`. `SkyColor`
builds a Minecraft-style sky from `WorldRenderer` time-of-day colours: a vertical gradient (Void → Horizon haze
→ Zenith), a sunrise/sunset orange band near the horizon in the sun's azimuth (`SunsetColor`), procedural
**stars** (hashed direction grid, faded in at night and out toward the horizon), and **textured sun and moon**
billboards (`CelestialBillboard` projects the view ray onto a tangent plane). The sun/moon textures are the real
pack assets (`WorldRenderer.LoadSkyTextures`); with no pack they fall back to a white pixel. The moon sits
opposite the sun (`-uSunDirection`), each hidden below its own horizon, drawn with a dedicated nearest/clamp
celestial sampler so the small disc stays crisp. **Distance fog** melts lit geometry into `HorizonColor`
between `FogStart`/`FogEnd` (0.72–0.97 × the horizon distance), hiding the load boundary against the sky. All
sky/sun/star/fog colours are `WorldRenderer` C# functions (sharing `DayTime`/`SunHeight`/`DayFactor`), so
retuning needs no recompile.

**Per-dimension visuals (generic — no per-dimension code).** `RenderWorld` reads `HasSky`, `FogColor`,
`AmbientLight` off the client world each frame (set on a dimension change, see [networking.md](networking.md)).
When `HasSky` is false (a sunless dimension like the Nether) the sky functions return a flat `FogColor`, the
directional sun term is forced off (no shadow pass), and `AmbientFloor` (`uAmbientFloor`) is a dimension-wide
minimum light so unlit ground isn't pitch black. The Overworld leaves all three at defaults, so nothing changes
there; the engine names no dimension (the Nether's values live in `NetherDimension`).

## Water surface

Water is shaded specially **in `Composition.wgsl`** — deferred-correct, since composition already reconstructs
world position from depth and holds the sun/sky/shadow terms — with **no extra pass, framebuffer, or vertex
attribute**. The mesher bakes `RenderMaterial.Water` into the face's material bits, which the geometry shader
encodes as **`normal.w ≈ 0.75`** in the G-buffer (distinct from lit solid 0.5 and unlit 1.0). Composition
detects a water pixel by a snug band around it (`WaterFlagLo/Hi`) and adds, on top of the already-blended
translucent tint: an animated **wave normal** (`WaveNormal` — six summed directional sine wavelets over the
surface world XZ, scrolled by `uTime`; the height is never needed, the analytic xz-gradient *is* the normal —
only the top face ripples), a **Schlick Fresnel** mix toward `SkyColor(reflect(-V, N))` (the same sky function
the background paints, so the water mirrors the live gradient/sun/moon/stars), and **Blinn-Phong sun + moon
specular** glints (the sun glint shadow-tested and scaled by `SunFade`; the moon glint, half-vector toward
`-sunDir`, fading in by `MoonFade` at night). All terms scale by the baked sky factor, so cave/overhang water
falls back to the plain tint for free. Look knobs are shader `const`s.

The subtlety: composition must *identify* water pixels, but the transparent pass blends all three MRTs, so a
flag in `normal.w` would blend into the background and become unreadable. Fix: the **transparent pipeline
alpha-blends only attachment 0 (diffuse)** and writes attachments 1 (normal) and 2 (light) **cleanly**
(overwrite, not blend) — so the front-most transparent surface writes its normal + water flag + light
deterministically. Diffuse still alpha-blends (translucency preserved). (Side effect: glass also writes its
front pane's own normal/light instead of blending them.)

**Underwater murk.** When the camera's eye sits inside a liquid block (`floor(cam)` block is liquid — corner-
origin, matching `PlayerPhysics`), `WorldRenderer` uploads `uUnderwater` + an `uUnderwaterColor` (deep water
blue, dimmed on the CPU by the current daylight) and the composition fogs the whole scene **and the background
sky** into it over a short distance, with a slight permanent near-camera tint — Minecraft's "you're underwater"
look. The HUD draws on top, so it stays clear.

## Overlays in the geometry pass

After the chunk geometry, several renderers draw into the same G-buffer pass:

- **`EntityRenderer`** — mobs/animals/dropped items/remote players as Bedrock-geo box models (dynamic-offset
  `{model, light}` slot UBO over a shared entity pipeline). Box UV follows Minecraft's `ModelPart$Cube` unwrap;
  living entities get a top/bottom UV swap (`flipUv: true`) reproducing the `LivingEntityRenderer` Y-flip while
  block entities keep the raw unwrap. A per-draw tint flashes red while `HurtTime` is non-zero. Players draw
  their main-hand item off the right-arm bone (`DrawHeldItem`) so it swings with the walk cycle. See
  [entities.md](entities.md).
- **`BlockEntityRenderer`** — blocks flagged `RendersAsBlockEntity` (chests) are skipped by the chunk mesher
  and drawn here as box models, oriented by the block's stored facing; the chest lid eases open when a screen
  is open. The same prebuilt model backs the block's inventory icon and held viewmodel. See
  [inventory.md](inventory.md).
- **`PlayerController.Render`** — the block-targeting outline + the survival breaking-crack overlay
  (`destroy_stage_0..9`). The break overlay masks the normal/light MRTs so the block keeps its geometry-pass
  lighting and the cracks read as part of the surface.
- **`ChunkBorderRenderer`** — the F4 debug overlay: wireframe boxes (the shared `OutlineRenderer` cube) around
  the chunks near the player, the current chunk highlighted, depth-tested against terrain.
- **`HeldItemRenderer`** (last) — the first-person held-item viewmodel, pinned to a fixed view-space pose
  composed from the resource pack's `display.firstperson_righthand` transform + MC's hand/swing constants
  (`displayMatrix · swing · translate(ITEM_POS)`); a held block draws as its 3D model, a flat item as its
  extruded sprite. It must not disturb the shared depth (composition reconstructs world position from it), so it
  sits on top of the world without clearing depth. See [state-gameloop.md](state-gameloop.md).

## Distant horizon (LOD)

**Full per-block detail to the render distance — the only LOD is the distant horizon beyond it.** Loaded chunks
(≤ `ServerNetwork.ViewDistance`) always mesh at full detail; coarse terrain only appears *past* the render
distance, where it is far and fog-hidden — a second streaming channel of surface-only columns letting terrain
recede to a fogged horizon `LodRingChunks` (default 64) chunks past the render distance, the Distant-Horizons
look. Far rings are 4×/16× cheaper (coarser stride) and fog-occluded. The data model, server-side LOD
generation/decoration, the packet, and the split-priority mesh worker live elsewhere — see
[world-model.md](world-model.md), [worldgen.md](worldgen.md), [networking.md](networking.md),
[threading.md](threading.md). The rendering side:

**Client render data + mesh.** On the main thread `WorldClient.DrainLodRenderReady` creates a GPU-free
`LodRenderData` per region; the **shared mesh pool** meshes it (`ChunkMesher.AddLodColumnRegionToVao` — flat-top
+ skirt emission driven by packed columns, tops greedy-merged, skirts per-cell, `meshStep` selecting the DH
detail ring by downsampling the stride-2 store by MAX surface so peaks/trees don't sink), and the main-thread
upload loop uploads it into a **second `ChunkMeshArena` (`LodArena`)** after the chunk uploads. `MeshStepFor`
sets the ring from the region's **horizontal** distance — stride-2 for the first ring past the render distance,
then 4/8/16, rings ≥ 1 region wide so adjacent regions never differ by >1 step. `EvictDistantLod` drops regions
past the cache distance. All GPU resource work is main-thread.

**Render integration.** The LOD arena is GPU-culled (`lodCull`, the region's full extent as the AABB cube) and
drawn in the geometry pass **after** the real chunks, sharing the G-buffer + geometry shader (its `GeoParams`
just selects `FadeMode = 1`). Because drawn geometry now reaches past the render distance, the composition
`uSkyDistance` + fog band widen to the LOD horizon. **No LOD in the shadow pass.**

**Dithered cross-fade (LOD morph, no pop).** Full-detail chunks dissolve into the horizon over a 32-block band
(`FadeBandWidth`) just inside the render distance instead of popping when a chunk loads/unloads at the edge.
`WorldGeometry.wgsl` passes `worldPos` and does a complementary **Bayer-4×4 dither discard** (chunk fades OUT
where `dither < fade`, LOD fades IN where `dither ≥ fade`, `fade` = camera distance across the band) so
**exactly one of {chunk, horizon} survives per pixel** — no gaps, no double-draw, no z-fight. The reverse-Z
depth precision keeps the coarse far ring from z-fighting the real chunks across the overlap. `WorldRenderer`
sets the `FadeMode` (0 chunk / 1 LOD) + band uniforms per batch; the band is pushed past the far plane (disabled)
when the horizon is dormant.

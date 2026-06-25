#version 410 core

// Deferred composition. Background pixels (the cleared far plane, no geometry) render a procedural
// Minecraft-style sky here: a time-of-day gradient + sunrise/sunset glow + a textured sun and moon (from
// the resource pack) + stars at night. Lit pixels combine the baked block + sky
// light with the depth-aware-upsampled half-res sun shadow (resolved in ShadowResolve.fs); water surfaces
// reflect that same sky. Finally everything fades into the horizon colour with distance fog so terrain
// melts into the sky at the render-distance edge.

in vec2 vTexCoord;

layout(location = 0) out vec4 outColor;

uniform sampler2D uDiffuse;
uniform sampler2D uNormal;
uniform sampler2D uDepth;
uniform sampler2D uLight;

// Sun colour/intensity for the current time of day. uLight.rgb is baked block light; uLight.a is the
// baked sky-light occlusion factor (0..1), multiplied here by the sun so day/night needs no remesh.
uniform vec3 uSunColor;

// Ambient sky light: soft fill in daytime shadows, dim moonlight at night. Like the sun it is scaled by
// uLight.a, so it lights only sky-exposed surfaces -- sky-occluded caves (uLight.a = 0) receive none and
// stay dark unless a block light reaches them.
uniform vec3 uSkyAmbient;

// Half-resolution resolved sun shadow (from ShadowResolve.fs): r = shadow factor (1 lit .. 0 shadowed),
// g = normalized view depth. Depth-aware-upsampled to full res below. uShadowDistance is the far shadow
// distance used to denormalize g back to view-space units.
uniform sampler2D uShadowResolved;
uniform float uShadowDistance;

// uViewProjectionInv reconstructs world position from the camera depth buffer; uView gives view-space depth
// (for the upsample's per-tap depth comparison and distance fog). uSunFade (0..1) scales the whole
// directional sun term and gates shadow sampling: it ramps to 0 as the sun reaches the horizon (and is 0
// when the sun is down / the shadow passes were skipped), so dusk fades smoothly to ambient with no pop.
uniform mat4 uViewProjectionInv;
uniform mat4 uView;
uniform float uSunFade;

// uShadowStrength (0..1): how dark a fully-shadowed surface goes. 1 = the raw shadow (can reach black);
// lower lifts the floor (shadow never below 1-strength) so sun shadows aren't crushed. Only touches the
// direct-sun term -- ambient sky fill and caves are untouched.
uniform float uShadowStrength;

// Graphics option: 0 disables sun shadows entirely (the depth + resolve passes are skipped, so the bound
// shadow buffer is stale). Surfaces then read as fully lit (shadow = 1) instead of sampling that stale buffer.
uniform float uShadowsEnabled;

// Debug (F7): raw shadow factor as greyscale (white = lit, black = shadowed).
uniform float uDebugShadow;

// Floor so a surface reached by no light at all isn't a literal void: a cave with no torch reads as nearly
// black (bring a torch). Driven by the Brightness graphics option (GraphicsSettings.Brightness); 0 = pure black.
uniform vec3 uMinLight;

// Water shading (Tier B): a face flagged with normal.w ~ 0.75 (baked by the mesher) gets animated sine-wave
// normals + a Fresnel reflection of the sky (the same SkyColor that paints the background) + a sun specular
// glint, all here in the deferred pass (no extra render pass). uCameraPos reconstructs the view vector,
// uSunDirection is the unit vector toward the sun, uTime scrolls the waves. The reflection/specular are
// scaled by the baked sky factor and uSunFade, so cave water and night water behave correctly with no
// special-casing. uCameraPos also gives the background view-ray direction for the sky.
uniform vec3 uCameraPos;
uniform vec3 uSunDirection;
uniform float uTime;

// Moonlight specular on water at night: the moon sits opposite the sun, uMoonFade ramps in as the sun sets
// (mirroring uSunFade), uMoonColor is the cool moonlight tint. Lets the moon glint on water once the sun's
// own (uSunFade-gated) specular has faded out.
uniform float uMoonFade;
uniform vec3 uMoonColor;

// --- Sky (background + water reflection) -------------------------------------------------------------
uniform vec3 uSkyColor;         // zenith colour (time of day)
uniform vec3 uHorizonColor;     // horizon haze colour (also the distance-fog colour)
uniform vec3 uVoidColor;        // colour below the horizon
uniform vec3 uSunsetColor;      // orange horizon glow toward the sun (~0 away from dawn/dusk)
uniform float uStarBrightness;  // 0 by day .. ~1 at night
uniform sampler2D uSunTexture;  // sun.png from the resource pack
uniform sampler2D uMoonTexture; // full_moon.png from the resource pack
uniform float uSunSize;         // tan of the sun billboard's half-angle (tunable, see WorldRenderer)
uniform float uMoonSize;
// View-space distance separating the cleared far plane (sky) from the farthest drawn terrain.
uniform float uSkyDistance;

// Distance fog: terrain fades into uHorizonColor between these view-space distances.
uniform float uFogStart;
uniform float uFogEnd;

// Joint-bilateral upsample sharpness (in normalized-depth units, i.e. view depth / uShadowDistance). Larger
// = a smaller depth difference rejects a tap, so the half-res shadow doesn't bleed across silhouette edges;
// too large and same-surface taps get rejected too (the half-res shadow shows through as blocky). Tunable.
const float DepthSharpness = 256.0;

// --- Water look (Tier B) tunables ---
// Detection band for the mesher's water flag in the G-buffer normal.w. The mesher writes normal.w = 0.5 for
// water; EncodeNormal stores 0.5*0.5+0.5 = 0.75, which Rgba8 quantizes to a deterministic 191/255 ~= 0.749
// (the flag is a flat per-face attribute and attachment 1 is written with blending OFF, so there is no
// interpolation/blend drift). The band is kept snug around that value: lit solid (input 0 -> 0.502) and unlit
// geometry (input 1 -> 1.0) are far outside. Any future RenderMaterial given its own mesher w-mapping must
// encode OUTSIDE this band (i.e. avoid input w in [0.4, 0.6)) or pick its own band.
const float WaterFlagLo = 0.7;
const float WaterFlagHi = 0.8;
// Animated surface: three summed directional sine waves. WaveAmp = per-wave slope, WaveFreq = base spatial
// frequency (per world block), WaveSpeed = scroll rate. Larger = choppier, smaller = glassier.
const float WaveAmp = 0.05;
const float WaveFreq = 0.9;
const float WaveSpeed = 1.3;
// Fresnel reflectance at normal incidence (water ~0.02): higher = more mirror-like looking straight down.
const float WaterF0 = 0.02;
// Sun specular glint: exponent (higher = tighter highlight) and gain.
const float WaterSpecExp = 220.0;
const float WaterSpecGain = 2.0;

vec3 PositionFromDepth(float depth)
{
	vec4 clipSpace = vec4(vTexCoord*2 - 1, depth, 1);
	vec4 homogenousCoord = uViewProjectionInv*clipSpace;
	return homogenousCoord.xyz/homogenousCoord.w;
}

float hash13(vec3 p3)
{
	p3 = fract(p3 * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

// Sparse round stars from a hashed direction grid: each cell may hold one star at a random sub-cell offset.
float Stars(vec3 dir)
{
	vec3 p = dir * 110.0;
	vec3 cell = floor(p);
	float rnd = hash13(cell);
	if (rnd < 0.972) return 0.0;
	vec3 center = vec3(hash13(cell + 11.5), hash13(cell + 23.7), hash13(cell + 41.3));
	float d = length(fract(p) - center);
	return smoothstep(0.13, 0.0, d) * ((rnd - 0.972) / 0.028);
}

// A textured celestial billboard (sun/moon) addressed by the angle between the view ray and its direction.
// Returns the [0,1] quad uv in .xy and 1 in .w when the ray hits the quad (.w 0 = miss / behind the camera).
vec4 CelestialBillboard(vec3 dir, vec3 toBody, float size)
{
	float along = dot(dir, toBody);
	if (along <= 0.0) return vec4(0.0);
	vec3 right = normalize(cross(vec3(0, 1, 0), toBody));
	vec3 up = cross(toBody, right);
	vec3 proj = dir / along;            // where the ray pierces the tangent plane one unit out along toBody
	float u = dot(proj, right);
	float v = dot(proj, up);
	if (abs(u) > size || abs(v) > size) return vec4(0.0);
	return vec4(vec2(u, v) / (2.0*size) + 0.5, 0.0, 1.0);
}

// Animated water normal from the analytic gradient of three summed directional sine waves, scrolled by time.
// Purely in-shader (no remesh); p is the world-space XZ of the water surface so waves are seamless across chunks.
vec3 WaveNormal(vec2 p, float t)
{
	vec2 d1 = normalize(vec2(1.0, 0.4));
	vec2 d2 = normalize(vec2(-0.3, 1.0));
	vec2 d3 = normalize(vec2(0.8, -0.6));
	float k1 = WaveFreq;
	float k2 = WaveFreq*1.7;
	float k3 = WaveFreq*0.6;
	vec2 g = vec2(0.0);
	g += d1*(WaveAmp*k1*cos(dot(p, d1)*k1 + t*WaveSpeed));
	g += d2*(WaveAmp*k2*cos(dot(p, d2)*k2 + t*WaveSpeed*0.8));
	g += d3*(WaveAmp*k3*cos(dot(p, d3)*k3 + t*WaveSpeed*1.3));
	return normalize(vec3(-g.x, 1.0, -g.y));
}

// The Minecraft-style sky in a given direction: a time-of-day vertical gradient + sunrise/sunset glow +
// stars + textured sun/moon billboards. Used both for the background and (via reflect()) for water, so the
// water mirrors the actual sky -- the sun glints, the moon and stars reflect at night.
vec3 SkyColor(vec3 dir)
{
	float h = dir.y;

	// Vertical gradient: void below the horizon, horizon haze at h=0, zenith colour overhead.
	vec3 sky = mix(uHorizonColor, uSkyColor, smoothstep(0.0, 0.55, h));
	sky = mix(sky, uVoidColor, smoothstep(0.0, -0.12, h));

	// Sunrise/sunset orange, concentrated near the horizon in the sun's azimuth.
	vec3 hd = normalize(vec3(dir.x, 0.0, dir.z) + 1e-5);
	vec3 sd = normalize(vec3(uSunDirection.x, 0.0, uSunDirection.z) + 1e-5);
	float glow = pow(max(dot(hd, sd), 0.0), 4.0) * exp(-abs(h)*6.0);
	sky += uSunsetColor * glow;

	// Stars (faded out into the horizon haze and during the day).
	if (uStarBrightness > 0.001)
		sky += vec3(Stars(dir)) * uStarBrightness * smoothstep(-0.05, 0.25, h);

	// Sun, hidden once it dips below its horizon.
	float sunVis = smoothstep(-0.08, 0.06, uSunDirection.y);
	if (sunVis > 0.0)
	{
		vec4 b = CelestialBillboard(dir, uSunDirection, uSunSize);
		if (b.w > 0.0)
		{
			vec3 sunCol = texture(uSunTexture, b.xy).rgb * uSunColor;
			sky += sunCol * sunVis;
		}
	}

	// Moon, opposite the sun.
	vec3 toMoon = -uSunDirection;
	float moonVis = smoothstep(-0.08, 0.06, toMoon.y);
	if (moonVis > 0.0)
	{
		vec4 b = CelestialBillboard(dir, toMoon, uMoonSize);
		if (b.w > 0.0)
		{
			vec3 moonCol = texture(uMoonTexture, b.xy).rgb;
			sky += moonCol * moonVis;
		}
	}

	return sky;
}

// Depth-aware 2x2 upsample of the half-res shadow: bilinear weights modulated by how close each half-res
// tap's stored depth is to this pixel's, so foreground shadow doesn't leak onto background across an edge.
float UpsampleShadow(float fragNormDepth)
{
	vec2 halfSize = vec2(textureSize(uShadowResolved, 0));
	vec2 coord = vTexCoord*halfSize - 0.5;
	vec2 base = floor(coord);
	vec2 f = coord - base;

	float sumShadow = 0.0;
	float sumWeight = 0.0;
	for (int dy = 0; dy < 2; dy++)
	for (int dx = 0; dx < 2; dx++)
	{
		ivec2 t = clamp(ivec2(base) + ivec2(dx, dy), ivec2(0), ivec2(halfSize) - 1);
		vec2 s = texelFetch(uShadowResolved, t, 0).rg;
		float bilinear = (dx == 0 ? 1.0 - f.x : f.x)*(dy == 0 ? 1.0 - f.y : f.y);
		float depthWeight = exp(-abs(s.g - fragNormDepth)*DepthSharpness);
		// Tiny floor so a pixel whose every tap is a depth mismatch (thin geometry) still falls back to a
		// plain bilinear sample instead of dividing by zero.
		float w = bilinear*(depthWeight + 0.003);
		sumShadow += s.r*w;
		sumWeight += w;
	}
	return sumWeight > 0.0 ? sumShadow/sumWeight : 1.0;
}

// Distance fog: melt geometry into the horizon colour at the render-distance edge (and into darkness at
// night, since the horizon colour itself dims), hiding the hard chunk-load boundary against the sky.
vec3 ApplyFog(vec3 color, float viewDepth)
{
	float fog = clamp((viewDepth - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
	return mix(color, uHorizonColor, fog*fog);
}

vec4 GetColor(vec3 worldPos, float viewDepth)
{
	vec4 diffuse = texture(uDiffuse, vTexCoord);
	if (diffuse.a == 0) discard;

	vec4 normal = texture(uNormal, vTexCoord);
	//If w value of normal is 1 dont apply lighting (eg. bounding boxes)
	if (normal.w == 1) return diffuse;

	vec4 lightSample = texture(uLight, vTexCoord);

	// Sample the resolved shadow only where the directional sun term can matter (same early-outs the resolve
	// pass used): daytime (uSunFade > 0), shadows enabled, and a sky-reached surface (lightSample.a). Past the
	// shadow distance there is no coverage. Everything else is fully lit (shadow = 1).
	float shadow = 1.0;
	if (uShadowsEnabled > 0.5 && uSunFade > 0.0 && lightSample.a > 0.004 && viewDepth < uShadowDistance)
		shadow = UpsampleShadow(viewDepth/uShadowDistance);

	if (uDebugShadow > 0.5) return vec4(vec3(shadow), 1.0);

	// Lift the shadow floor by uShadowStrength so a fully-shadowed surface keeps some direct sun instead of
	// crushing to ambient-only (the "shadows too dark" knob). Only the direct-sun term is affected.
	float litShadow = mix(1.0, shadow, uShadowStrength);

	// Sky-exposed light: direct sun (shadowed, daytime; uSunFade ramps it to 0 at the horizon with no pop)
	// plus ambient sky fill / moonlight (unshadowed) -- both gated by the baked sky factor, so sky-occluded
	// caves get neither. Block light (torches) is separate and reaches caves; whichever is brighter wins.
	vec3 skyLight = lightSample.a*(litShadow*uSunColor*uSunFade + uSkyAmbient);
	vec3 light = max(lightSample.rgb, skyLight);

	vec3 baseColor = diffuse.rgb * max(light, uMinLight);

	// Water surface (mesher-flagged normal.w ~ 0.75): add animated normals + a Fresnel reflection of the sky
	// (the same SkyColor the background uses, so water mirrors the real sun/moon/stars) + a sun specular
	// glint on top of the Tier-A lit translucent water (baseColor). Everything is scaled by the baked sky
	// factor and uSunFade, so cave/overhang water (lightSample.a ~ 0) and night water fall straight back to
	// the plain look with no special-casing.
	if (normal.w > WaterFlagLo && normal.w < WaterFlagHi)
	{
		vec3 faceN = normalize(normal.xyz*2.0 - 1.0);
		vec3 N = faceN.y > 0.5 ? WaveNormal(worldPos.xz, uTime) : faceN;
		vec3 V = normalize(uCameraPos - worldPos);
		float fres = WaterF0 + (1.0 - WaterF0)*pow(1.0 - max(dot(N, V), 0.0), 5.0);
		vec3 skyRefl = SkyColor(reflect(-V, N));
		vec3 H = normalize(V + uSunDirection);
		float spec = pow(max(dot(N, H), 0.0), WaterSpecExp)*uSunFade*shadow*lightSample.a*WaterSpecGain;
		// Moon glint: opposite the sun, gated to night by uMoonFade (the sun shadow map is the sun's, so the
		// moon term isn't shadowed by it).
		vec3 Hm = normalize(V - uSunDirection);
		float specMoon = pow(max(dot(N, Hm), 0.0), WaterSpecExp)*uMoonFade*lightSample.a*WaterSpecGain;
		vec3 col = mix(baseColor, skyRefl, fres*lightSample.a) + uSunColor*spec + uMoonColor*specMoon;
		return vec4(ApplyFog(col, viewDepth), diffuse.a);
	}

	return vec4(ApplyFog(baseColor, viewDepth), diffuse.a);
}

void main()
{
	float depthRaw = texture(uDepth, vTexCoord).x;
	vec3 worldPos = PositionFromDepth(depthRaw*2 - 1);
	float viewDepth = -(uView*vec4(worldPos, 1)).z;

	// Background = the cleared far plane (a constant view depth = the far clip plane), well beyond the
	// farthest drawn terrain. Render the sky along the pixel's view ray; reuse worldPos as the far ray point.
	if (viewDepth >= uSkyDistance)
	{
		outColor = vec4(SkyColor(normalize(worldPos - uCameraPos)), 1.0);
		return;
	}

	outColor = GetColor(worldPos, viewDepth);
}

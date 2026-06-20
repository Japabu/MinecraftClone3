#version 410 core

// Deferred composition. The 12-tap sun-shadow PCF runs in ShadowResolve.fs at half res; this pass
// depth-aware-upsamples that result and combines it with the baked block + sky light.

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
// (for the upsample's per-tap depth comparison). uSunFade (0..1) scales the whole directional sun term and
// gates shadow sampling: it ramps to 0 as the sun reaches the horizon (and is 0 when the sun is down / the
// shadow passes were skipped), so dusk fades smoothly to ambient with no pop.
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
// normals + a Fresnel reflection of an analytic sky + a sun specular glint, all here in the deferred pass (no
// extra render pass). uCameraPos reconstructs the view vector, uSunDirection is the unit vector toward the
// sun, uTime scrolls the waves. The reflection/specular are scaled by the baked sky factor and uSunFade, so
// cave water and night water behave correctly with no special-casing.
uniform vec3 uCameraPos;
uniform vec3 uSunDirection;
uniform float uTime;

// Joint-bilateral upsample sharpness (in normalized-depth units, i.e. view depth / uShadowDistance). Larger
// = a smaller depth difference rejects a tap, so the half-res shadow doesn't bleed across silhouette edges;
// too large and same-surface taps get rejected too (the half-res shadow shows through as blocky). Tunable.
const float DepthSharpness = 256.0;

vec3 PositionFromDepth(float depth)
{
	vec4 clipSpace = vec4(vTexCoord*2 - 1, depth, 1);
	vec4 homogenousCoord = uViewProjectionInv*clipSpace;
	return homogenousCoord.xyz/homogenousCoord.w;
}

float FragViewDepth()
{
	vec3 worldPos = PositionFromDepth(texture(uDepth, vTexCoord).x*2 - 1);
	return -(uView*vec4(worldPos, 1)).z;
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

// --- Water look (Tier B) tunables ---
// Detection band for the mesher's water flag in the G-buffer normal.w. The mesher writes normal.w = 0.5 for
// water; EncodeNormal stores 0.5*0.5+0.5 = 0.75, which Rgba8 quantizes to a deterministic 191/255 ≈ 0.749
// (the flag is a flat per-face attribute and attachment 1 is written with blending OFF, so there is no
// interpolation/blend drift). The band is kept snug around that value: lit solid (input 0 → 0.502) and unlit
// geometry (input 1 → 1.0) are far outside. Any future RenderMaterial given its own mesher w-mapping must
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
// Analytic sky reflection (no skybox exists): gradient gains over uSkyAmbient + a reflected sun disc, so the
// reflection tracks the time of day (blue by day, warm at dusk, moon-dim at night).
const float SkyHorizonGain = 5.0;
const float SkyZenithGain = 3.0;
const float SkyHorizonSunTint = 0.15;
const float SunDiscExp = 350.0;
const float SunDiscGain = 1.5;

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

// Analytic sky colour in a given direction, built from the day/night uniforms (there is no skybox): a
// horizon->zenith gradient over uSkyAmbient plus a sharp reflected sun disc, both faded by uSunFade.
vec3 SkyColor(vec3 dir)
{
	float up = clamp(dir.y, 0.0, 1.0);
	vec3 zenith = uSkyAmbient*SkyZenithGain;
	vec3 horizon = uSkyAmbient*SkyHorizonGain + uSunColor*uSunFade*SkyHorizonSunTint;
	vec3 sky = mix(horizon, zenith, up);
	float sd = max(dot(dir, uSunDirection), 0.0);
	sky += uSunColor*uSunFade*(pow(sd, SunDiscExp)*SunDiscGain);
	return sky;
}

vec4 GetColor()
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
	if (uShadowsEnabled > 0.5 && uSunFade > 0.0 && lightSample.a > 0.004)
	{
		float fragDepth = FragViewDepth();
		if (fragDepth < uShadowDistance)
			shadow = UpsampleShadow(fragDepth/uShadowDistance);
	}

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

	// Water surface (mesher-flagged normal.w ~ 0.75): add animated normals + a Fresnel reflection of the
	// analytic sky + a sun specular glint on top of the Tier-A lit translucent water (baseColor). Everything
	// is scaled by the baked sky factor and uSunFade, so cave/overhang water (lightSample.a ~ 0) and night
	// water (uSunFade ~ 0) fall straight back to the plain look with no special-casing.
	if (normal.w > WaterFlagLo && normal.w < WaterFlagHi)
	{
		vec3 worldPos = PositionFromDepth(texture(uDepth, vTexCoord).x*2 - 1);
		vec3 faceN = normalize(normal.xyz*2.0 - 1.0);
		vec3 N = faceN.y > 0.5 ? WaveNormal(worldPos.xz, uTime) : faceN;
		vec3 V = normalize(uCameraPos - worldPos);
		float fres = WaterF0 + (1.0 - WaterF0)*pow(1.0 - max(dot(N, V), 0.0), 5.0);
		vec3 skyRefl = SkyColor(reflect(-V, N));
		vec3 H = normalize(V + uSunDirection);
		float spec = pow(max(dot(N, H), 0.0), WaterSpecExp)*uSunFade*shadow*lightSample.a*WaterSpecGain;
		vec3 col = mix(baseColor, skyRefl, fres*lightSample.a) + uSunColor*spec;
		return vec4(col, diffuse.a);
	}

	return vec4(baseColor, diffuse.a);
}

void main()
{
	outColor = GetColor();
}

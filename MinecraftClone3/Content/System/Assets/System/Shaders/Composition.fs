#version 410 core

// One entry per MAX_CASCADES (only used by the F6 cascade-tint debug now; the cascaded PCF itself moved to
// ShadowResolve.fs, which runs at half res -- see that shader + WorldRenderer.DrawShadowResolve).
#define MAX_CASCADES 4

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
// g = normalized view depth, b = cascade index. Depth-aware-upsampled to full res below. uShadowMaxDepth is
// the far shadow distance (uCascadeSplits[last]) used to denormalize g back to view-space units.
uniform sampler2D uShadowResolved;
uniform float uShadowMaxDepth;

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

// Debug (F7): raw shadow factor as greyscale (white = lit, black = shadowed). Debug (F6): tint by cascade
// (green->yellow->orange->red, near->far; untinted past the shadow distance) so the CSM split is visible.
uniform float uDebugShadow;
uniform float uDebugCascade;

// Floor so a surface reached by no light at all isn't a literal void: a cave with no torch reads as nearly
// black (bring a torch). Raise for a brighter global ambient; 0 = pure black.
const vec3 MinLight = vec3(0.01);

// Joint-bilateral upsample sharpness (in normalized-depth units, i.e. view depth / uShadowMaxDepth). Larger
// = a smaller depth difference rejects a tap, so the half-res shadow doesn't bleed across silhouette edges;
// too large and same-surface taps get rejected too (the half-res shadow shows through as blocky). Tunable.
const float DepthSharpness = 256.0;

// Cascade debug tints (F6), near -> far. One entry per MAX_CASCADES.
const vec3 CascadeColor[MAX_CASCADES] = vec3[](
	vec3(0.3, 1.0, 0.3), vec3(1.0, 1.0, 0.2), vec3(1.0, 0.6, 0.1), vec3(1.0, 0.25, 0.25));

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
	int debugCascade = -1;
	if (uShadowsEnabled > 0.5 && uSunFade > 0.0 && lightSample.a > 0.004)
	{
		float fragDepth = FragViewDepth();
		if (fragDepth < uShadowMaxDepth)
		{
			shadow = UpsampleShadow(fragDepth/uShadowMaxDepth);
			ivec2 hs = textureSize(uShadowResolved, 0);
			ivec2 t = clamp(ivec2(vTexCoord*vec2(hs)), ivec2(0), hs - 1);
			debugCascade = int(texelFetch(uShadowResolved, t, 0).b*float(MAX_CASCADES) + 0.5) - 1;
		}
	}

	if (uDebugShadow > 0.5) return vec4(vec3(shadow), 1.0);
	if (uDebugCascade > 0.5)
	{
		vec3 tint = debugCascade < 0 ? vec3(0.5) : CascadeColor[debugCascade];
		return vec4(diffuse.rgb*tint, diffuse.a);
	}

	// Lift the shadow floor by uShadowStrength so a fully-shadowed surface keeps some direct sun instead of
	// crushing to ambient-only (the "shadows too dark" knob). Only the direct-sun term is affected.
	float litShadow = mix(1.0, shadow, uShadowStrength);

	// Sky-exposed light: direct sun (shadowed, daytime; uSunFade ramps it to 0 at the horizon with no pop)
	// plus ambient sky fill / moonlight (unshadowed) -- both gated by the baked sky factor, so sky-occluded
	// caves get neither. Block light (torches) is separate and reaches caves; whichever is brighter wins.
	vec3 skyLight = lightSample.a*(litShadow*uSunColor*uSunFade + uSkyAmbient);
	vec3 light = max(lightSample.rgb, skyLight);

	return vec4(diffuse.rgb * max(light, MinLight), diffuse.a);
}

void main()
{
	outColor = GetColor();
}

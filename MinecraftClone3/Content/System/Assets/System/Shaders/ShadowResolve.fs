#version 410 core

// Half-resolution sun-shadow resolve. The expensive 12-tap PCF used to run per pixel inside the composition
// pass (~45% of GPU frame time on a fill-limited GPU); it now runs here at HALF resolution (quarter the
// invocations) and writes a small shadow buffer the composition pass depth-aware-upsamples back to full res.
// Output: r = sun shadow factor (1 lit .. 0 shadowed), g = normalized view depth (for the composition's
// bilateral upsample to reject taps across silhouettes).

in vec2 vTexCoord;

layout(location = 0) out vec4 outShadow;

uniform sampler2D uNormal;
uniform sampler2D uDepth;
uniform sampler2D uLight;

uniform sampler2DShadow uShadowMap;
uniform mat4 uViewProjectionInv;
uniform mat4 uView;
uniform mat4 uLightViewProj;
uniform float uShadowTexel;
uniform float uShadowDistance;
uniform float uSunFade;
uniform float uShadowsEnabled;
uniform float uShadowSoftness;

// Must match ShadowFramebuffer.ShadowMapSize.
const float ShadowTexel = 1.0 / 1024.0;
const float NormalBias = 2.0;
const float DepthBias = 0.0005;

// 12-tap Poisson disc (unit radius). Rotated per pixel so the sparse taps read as smooth noise, not bands.
const vec2 Poisson[12] = vec2[](
	vec2(-0.326, -0.406), vec2(-0.840, -0.074), vec2(-0.696,  0.457), vec2(-0.203,  0.621),
	vec2( 0.962, -0.195), vec2( 0.473, -0.480), vec2( 0.519,  0.767), vec2( 0.185, -0.893),
	vec2( 0.507,  0.064), vec2( 0.896,  0.412), vec2(-0.322, -0.933), vec2(-0.792, -0.598));

vec4 DecodeNormal(vec4 normal)
{
	return normal*2 - 1;
}

vec3 PositionFromDepth(float depth)
{
	vec4 clipSpace = vec4(vTexCoord*2 - 1, depth, 1);
	vec4 homogenousCoord = uViewProjectionInv*clipSpace;
	return homogenousCoord.xyz/homogenousCoord.w;
}

// 1.0 = fully sun-lit, 0.0 = fully shadowed. 12-tap rotated-Poisson hardware PCF.
float SampleShadow(vec3 worldPos, vec3 normal, mat2 rot)
{
	vec3 offsetPos = worldPos + normal*(uShadowTexel*NormalBias);
	vec4 lc = uLightViewProj*vec4(offsetPos, 1);
	vec3 p = lc.xyz/lc.w*0.5 + 0.5;
	if (p.z > 1.0) return 1.0;

	float refDepth = p.z - DepthBias;
	float sum = 0.0;
	for (int i = 0; i < 12; i++)
	{
		vec2 off = rot*Poisson[i]*(uShadowSoftness*ShadowTexel);
		sum += texture(uShadowMap, vec3(p.xy + off, refDepth));
	}
	return sum/12.0;
}

float ShadowFactor(vec3 worldPos, vec3 normal, float viewDepth)
{
	// Per-pixel rotation of the Poisson disc (interleaved gradient noise) so the soft penumbra dithers
	// instead of banding.
	float ign = fract(52.9829189*fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
	float a = ign*6.2831853;
	mat2 rot = mat2(cos(a), -sin(a), sin(a), cos(a));

	float lit = SampleShadow(worldPos, normal, rot);

	// Fade the shadow out over the last 10% of the shadow distance so the far edge of coverage does not
	// pop from shadowed to lit.
	float fade = clamp((uShadowDistance - viewDepth)/(uShadowDistance*0.1), 0.0, 1.0);
	return mix(1.0, lit, fade);
}

void main()
{
	vec4 normal = texture(uNormal, vTexCoord);
	vec4 lightSample = texture(uLight, vTexCoord);

	float shadow = 1.0;
	float viewDepth = uShadowDistance;

	// Same early-outs as the old inline path: skip the PCF where the directional sun term can't matter --
	// unlit geometry (normal.w == 1), night/dusk (uSunFade ~ 0), sky-occluded surfaces (lightSample.a ~ 0),
	// and past the shadow distance. Those pixels write shadow = 1.
	if (normal.w != 1.0 && uShadowsEnabled > 0.5 && uSunFade > 0.0 && lightSample.a > 0.004)
	{
		vec3 worldPos = PositionFromDepth(texture(uDepth, vTexCoord).x*2 - 1);
		viewDepth = -(uView*vec4(worldPos, 1)).z;
		if (viewDepth < uShadowDistance)
			shadow = ShadowFactor(worldPos, DecodeNormal(normal).xyz, viewDepth);
	}

	float normDepth = clamp(viewDepth/uShadowDistance, 0.0, 1.0);
	outShadow = vec4(shadow, normDepth, 0.0, 1.0);
}

#version 410 core

in vec2 vTexCoord;

layout(location = 0) out vec4 outColor;

uniform sampler2D uDiffuse;
uniform sampler2D uNormal;
uniform sampler2D uDepth;
uniform sampler2D uLight;

// Sun colour/intensity for the current time of day. uLight.rgb is baked block light; uLight.a is the
// baked sky-light occlusion factor (0..1), multiplied here by the sun so day/night needs no remesh.
uniform vec3 uSunColor;

// Minimum brightness so surfaces with no light at all (e.g. a sealed cave at night) stay faintly visible.
const vec3 Ambient = vec3(0.2);

vec4 GetColor()
{
	vec4 diffuse = texture(uDiffuse, vTexCoord);
	if (diffuse.a == 0) discard;

	vec4 normal = texture(uNormal, vTexCoord);
	//If w value of normal is 1 dont apply lighting (eg. bounding boxes)
	if (normal.w == 1) return diffuse;

	vec4 lightSample = texture(uLight, vTexCoord);
	vec3 light = max(lightSample.rgb, lightSample.a * uSunColor);

	return vec4(diffuse.rgb * max(light, Ambient), diffuse.a);
}

vec4 DecodeNormal(vec4 normal)
{
	return normal*2 - 1;
}

void main()
{
	outColor = GetColor();
}

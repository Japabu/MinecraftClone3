#version 410 core

in vec2 vTexCoord;

layout(location = 0) out vec4 outColor;

uniform sampler2D uDiffuse;
uniform sampler2D uNormal;
uniform sampler2D uDepth;
uniform sampler2D uLight;

// Minimum brightness so surfaces with no nearby light source stay faintly visible
// (the world has no skylight/sunlight, only block-emitted light such as torches).
const vec3 Ambient = vec3(0.2);

vec4 GetColor()
{
	vec4 diffuse = texture(uDiffuse, vTexCoord);
	if (diffuse.a == 0) discard;

	vec4 normal = texture(uNormal, vTexCoord);
	//If w value of normal is 1 dont apply lighting (eg. bounding boxes)
	if (normal.w == 1) return diffuse;

	vec3 light = texture(uLight, vTexCoord).rgb;

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

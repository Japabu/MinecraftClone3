#version 410 core

in vec2 vTexCoord;

layout(location = 0) out vec4 outColor;

uniform mat4 uViewProjectionInv;
uniform vec3 uLightPosition;
uniform vec3 uLightColor;
uniform float uLightRange;

uniform sampler2D uNormal;
uniform sampler2D uDepth;

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

void main()
{
	vec4 normal = DecodeNormal(texture(uNormal, vTexCoord));
	float depth = texture(uDepth, vTexCoord).x * 2 - 1;
	vec3 position = PositionFromDepth(depth);

	vec3 pixelToLight = uLightPosition - position;
	float distanceSq = dot(pixelToLight, pixelToLight);
	float normalFactor = max(dot(normalize(pixelToLight), normal.xyz), 0);
	float attenuation = clamp(1 - distanceSq/(uLightRange*uLightRange), 0, 1);

	float ambient = 0.03;
	vec3 color = (normalFactor + ambient)*attenuation*uLightColor;
	color = pow(color, vec3(1/2.2));

	outColor = vec4(color, 1);
}

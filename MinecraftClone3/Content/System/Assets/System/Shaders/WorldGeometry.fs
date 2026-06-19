#version 410 core

in vec4 vTexCoord;
in vec4 vNormal;
in vec3 vColor;
in vec4 vLight;

layout(location = 0) out vec4 outDiffuse;
layout(location = 1) out vec4 outNormal;
// rgb = baked block light, a = baked sky-light factor (modulated by the sun in composition)
layout(location = 2) out vec4 outLight;

uniform sampler2DArray uTextures16;
uniform sampler2DArray uTextures64;
uniform sampler2DArray uTextures256;
uniform sampler2DArray uTextures1024;

uniform bool uCutoff;

vec4 GetDiffuse()
{
	//If w value of normal is 1 dont apply shading (eg. bounding boxes)
	if (vNormal.w == 1) return vec4(vColor, 1);

	//Get color from the right texture array
	vec4 texColor = vec4(0);
	if(vTexCoord.w == 0) texColor = texture(uTextures16, vTexCoord.xyz);
	else if(vTexCoord.w == 1) texColor = texture(uTextures64, vTexCoord.xyz);
	else if(vTexCoord.w == 2) texColor = texture(uTextures256, vTexCoord.xyz);
	else if(vTexCoord.w == 3) texColor = texture(uTextures1024, vTexCoord.xyz);
	texColor.rgb *= vColor;

	if(uCutoff && texColor.a < 0.5) discard;

	return texColor;
}

vec4 EncodeNormal(vec4 normal)
{
	return normal*0.5 + 0.5;
}

void main()
{
	vec4 diffuse = GetDiffuse();

	outDiffuse = diffuse;
	outNormal = EncodeNormal(vNormal);
	outLight = vLight;
}

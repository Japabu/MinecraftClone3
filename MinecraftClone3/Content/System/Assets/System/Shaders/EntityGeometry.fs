#version 410 core

in vec4 vTexCoord;
in vec4 vNormal;
in vec3 vColor;

layout(location = 0) out vec4 outDiffuse;
layout(location = 1) out vec4 outNormal;
layout(location = 2) out vec4 outLight;

uniform sampler2DArray uTextures16;
uniform sampler2DArray uTextures64;
uniform sampler2DArray uTextures256;
uniform sampler2DArray uTextures1024;

// One flat block+sky light value for the whole entity (sampled at its position CPU-side), so it darkens in
// shadow/caves and brightens in the open like the blocks around it.
uniform vec4 uLight;

vec4 SampleTexture()
{
	if(vTexCoord.w == 0) return texture(uTextures16, vTexCoord.xyz);
	if(vTexCoord.w == 1) return texture(uTextures64, vTexCoord.xyz);
	if(vTexCoord.w == 2) return texture(uTextures256, vTexCoord.xyz);
	if(vTexCoord.w == 3) return texture(uTextures1024, vTexCoord.xyz);
	return vec4(vColor, 1);
}

void main()
{
	vec4 texColor = SampleTexture();
	// Entity sheets have fully-transparent regions (the unused parts of the box-unwrap, capes, etc.); drop them.
	if(texColor.a < 0.5) discard;

	outDiffuse = vec4(texColor.rgb * vColor, 1);
	outNormal = vNormal*0.5 + 0.5;   // material .w = 0 => lit (receives sun/shadow + light in composition)
	outLight = uLight;
}

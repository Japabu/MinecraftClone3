#version 410 core

in vec3 vTexCoord;
flat in int vArrayId;
in vec3 vColor;
in float vShade;

out vec4 outColor;

uniform sampler2DArray uTextures16;
uniform sampler2DArray uTextures64;
uniform sampler2DArray uTextures256;
uniform sampler2DArray uTextures1024;

void main()
{
	vec4 c = vec4(vColor, 1);
	if (vArrayId == 0) c = texture(uTextures16, vTexCoord);
	else if (vArrayId == 1) c = texture(uTextures64, vTexCoord);
	else if (vArrayId == 2) c = texture(uTextures256, vTexCoord);
	else if (vArrayId == 3) c = texture(uTextures1024, vTexCoord);

	// Drop fully transparent texels so the icon keeps the framebuffer's transparent background.
	if (c.a < 0.1) discard;

	c.rgb *= vColor * vShade;
	outColor = c;
}

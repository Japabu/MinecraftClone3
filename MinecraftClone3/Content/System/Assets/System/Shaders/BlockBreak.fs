#version 410 core

in vec2 outTexCoord;

layout(location = 0) out vec4 outDiffuse;

uniform sampler2D uTexture;

void main()
{
	vec4 c = texture(uTexture, outTexCoord);
	if (c.a < 0.01) discard;
	outDiffuse = c;
}

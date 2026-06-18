#version 410 core

in vec2 outTexCoord;

out vec4 outColor;

uniform sampler2D uTexture;

void main()
{
	outColor = texture(uTexture, outTexCoord);
}

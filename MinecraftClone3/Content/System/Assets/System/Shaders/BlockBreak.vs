#version 410 core

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inTexCoord;

out vec2 outTexCoord;

uniform mat4 uTransform;

void main()
{
	gl_Position = uTransform*vec4(inPosition, 1);
	outTexCoord = inTexCoord;
}

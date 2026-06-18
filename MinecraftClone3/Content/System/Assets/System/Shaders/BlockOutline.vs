#version 410 core

layout(location = 0) in vec3 inPosition;

uniform mat4 uTransform;

void main()
{
	gl_Position = uTransform*vec4(inPosition, 1);
}

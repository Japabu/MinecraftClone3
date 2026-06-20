#version 410 core

layout(location = 0) in vec3 inPosition;

uniform mat4 uWorld;
uniform mat4 uLightViewProj;

void main()
{
	gl_Position = uLightViewProj*uWorld*vec4(inPosition, 1);
}

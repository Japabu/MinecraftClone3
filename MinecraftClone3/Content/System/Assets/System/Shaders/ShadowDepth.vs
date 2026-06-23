#version 410 core

layout(location = 0) in vec3 inPosition;

uniform mat4 uLightViewProj;

void main()
{
	// World-space baked positions (see WorldGeometry.vs) - no per-chunk model matrix.
	gl_Position = uLightViewProj*vec4(inPosition, 1);
}

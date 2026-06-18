#version 410 core

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inTexCoord;
layout(location = 2) in vec4 inNormal;
layout(location = 3) in vec3 inColor;
layout(location = 4) in vec3 inLight;

out vec4 vTexCoord;
out vec4 vNormal;
out vec3 vColor;
out vec3 vLight;

uniform mat4 uWorld;
uniform mat4 uView;
uniform mat4 uProjection;

void main()
{
	gl_Position = uProjection*uView*uWorld*vec4(inPosition, 1);

	vTexCoord = inTexCoord;
	vNormal = inNormal;
	vColor = inColor;
	vLight = inLight;
}

#version 410 core

layout(location = 0) in vec3 inPosition;

out vec2 vTexCoord;

void main()
{
	gl_Position = vec4(inPosition, 1);

	vTexCoord = vec2(inPosition.x*0.5 + 0.5, inPosition.y*0.5 + 0.5);
}

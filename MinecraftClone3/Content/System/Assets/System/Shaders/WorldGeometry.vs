#version 410 core

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inTexCoord;
layout(location = 2) in vec4 inNormal;
layout(location = 3) in vec3 inColor;
layout(location = 4) in vec4 inLight;

out vec4 vTexCoord;
out vec4 vNormal;
out vec3 vColor;
out vec4 vLight;

uniform mat4 uView;
uniform mat4 uProjection;

void main()
{
	// Positions are baked world-space at mesh time (no per-chunk model matrix), so all chunks share one
	// buffer set and draw with a single batched multidraw.
	gl_Position = uProjection*uView*vec4(inPosition, 1);

	vTexCoord = inTexCoord;
	vNormal = inNormal;
	vColor = inColor;
	vLight = inLight;
}

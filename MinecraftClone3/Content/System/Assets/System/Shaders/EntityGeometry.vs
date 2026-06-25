#version 410 core

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in uint inPacked;   // texId(16) | arrayId(2) | normalIndex(3) | material(2)
layout(location = 3) in vec4 inColor;    // tint RGBA8 (normalized)
layout(location = 4) in vec4 inLight;    // unused for entities (their light is the uLight uniform)

out vec4 vTexCoord;
out vec4 vNormal;
out vec3 vColor;

// Unlike chunks (which bake world-space positions), an entity is a small rigid model drawn with a per-entity
// (and per-animated-part) model matrix, so the vertex shader applies uModel here.
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

const vec3 Normals[6] = vec3[6](
	vec3( 1, 0, 0), vec3(-1, 0, 0),
	vec3( 0, 1, 0), vec3( 0,-1, 0),
	vec3( 0, 0, 1), vec3( 0, 0,-1));

void main()
{
	gl_Position = uProjection*uView*uModel*vec4(inPosition, 1);

	uint texId  = inPacked & 0xFFFFu;
	uint arrayId = (inPacked >> 16) & 0x3u;
	uint normalIndex = (inPacked >> 18) & 0x7u;

	bool noTex = (texId == 0xFFFFu);
	vTexCoord = vec4(inUv, noTex ? -1.0 : float(texId), noTex ? -1.0 : float(arrayId));

	// Transform the normal the same way as the position (direction => w=0) so it stays correct under the
	// model's rotation+yaw regardless of matrix convention; material flag 0 (= lit) in .w.
	vec3 n = normalize((uModel*vec4(Normals[normalIndex], 0.0)).xyz);
	vNormal = vec4(n, 0.0);
	vColor = inColor.rgb;
}

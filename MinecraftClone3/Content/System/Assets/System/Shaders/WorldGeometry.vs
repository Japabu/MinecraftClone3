#version 410 core

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in uint inPacked;   // texId(16) | arrayId(2) | normalIndex(3) | material(2)
layout(location = 3) in vec4 inColor;    // tint RGBA8 (normalized)
layout(location = 4) in vec4 inLight;    // RGBA8: rgb block light, a sky factor (normalized)

out vec4 vTexCoord;
out vec4 vNormal;
out vec3 vColor;
out vec4 vLight;

uniform mat4 uView;
uniform mat4 uProjection;

// The 6 axis normals a voxel face can have (indexed by the packed normalIndex).
const vec3 Normals[6] = vec3[6](
	vec3( 1, 0, 0), vec3(-1, 0, 0),
	vec3( 0, 1, 0), vec3( 0,-1, 0),
	vec3( 0, 0, 1), vec3( 0, 0,-1));
// Material → the old normal.w flag (0 lit, 0.5 water, 1 unlit), kept so EncodeNormal/Composition are unchanged.
const float Material[3] = float[3](0.0, 0.5, 1.0);

void main()
{
	// Positions are baked world-space at mesh time (no per-chunk model matrix), so all chunks share one
	// buffer set and draw with a single batched multidraw.
	gl_Position = uProjection*uView*vec4(inPosition, 1);

	uint texId  = inPacked & 0xFFFFu;
	uint arrayId = (inPacked >> 16) & 0x3u;
	uint normalIndex = (inPacked >> 18) & 0x7u;
	uint material = (inPacked >> 21) & 0x3u;

	// vTexCoord.xy = uv, .z = texId, .w = arrayId (the fragment shader selects the texture array by .w).
	// No-texture faces stored texId 0xFFFF; decode z AND w back to -1 so the FS matches no array (as before).
	bool noTex = (texId == 0xFFFFu);
	float tex = noTex ? -1.0 : float(texId);
	float arr = noTex ? -1.0 : float(arrayId);
	vTexCoord = vec4(inUv, tex, arr);
	vNormal = vec4(Normals[normalIndex], Material[material]);
	vColor = inColor.rgb;
	vLight = inLight;
}

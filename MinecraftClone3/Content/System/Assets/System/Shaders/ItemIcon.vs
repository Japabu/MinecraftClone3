#version 410 core

// Renders a single block's chunk-format mesh into an off-screen icon (see ItemIconRenderer). Same packed
// vertex layout as WorldGeometry.vs, but forward-shaded with a fixed per-face brightness (no G-buffer, no
// world light) so an inventory icon looks like Minecraft's isometric block render.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in uint inPacked;   // texId(16) | arrayId(2) | normalIndex(3) | material(2)
layout(location = 3) in vec4 inColor;    // tint RGBA8 (normalized)
layout(location = 4) in vec4 inLight;    // unused for icons

out vec3 vTexCoord;   // xy = uv, z = texId (texture-array layer)
flat out int vArrayId;
out vec3 vColor;
out float vShade;

uniform mat4 uView;
uniform mat4 uProjection;

// Fixed face shading indexed by the packed normalIndex (+X,-X,+Y,-Y,+Z,-Z): top brightest, bottom darkest,
// the four sides in between, matching Minecraft's vanilla block shading.
const float Shade[6] = float[6](0.6, 0.6, 1.0, 0.5, 0.8, 0.8);

void main()
{
	gl_Position = uProjection * uView * vec4(inPosition, 1);

	uint texId = inPacked & 0xFFFFu;
	uint arrayId = (inPacked >> 16) & 0x3u;
	uint normalIndex = (inPacked >> 18) & 0x7u;

	bool noTex = (texId == 0xFFFFu);
	vTexCoord = vec3(inUv, noTex ? -1.0 : float(texId));
	vArrayId = noTex ? -1 : int(arrayId);
	vColor = inColor.rgb;
	vShade = Shade[normalIndex];
}

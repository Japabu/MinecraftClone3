#version 410 core

in vec4 vTexCoord;
in vec4 vNormal;
in vec3 vColor;
in vec4 vLight;
in vec3 vWorldPos;

layout(location = 0) out vec4 outDiffuse;
layout(location = 1) out vec4 outNormal;
// rgb = baked block light, a = baked sky-light factor (modulated by the sun in composition)
layout(location = 2) out vec4 outLight;

uniform sampler2DArray uTextures16;
uniform sampler2DArray uTextures64;
uniform sampler2DArray uTextures256;
uniform sampler2DArray uTextures1024;

uniform bool uCutoff;

// LOD cross-fade (dithered morph between full-detail chunks and the Phase-2 horizon at the render-distance
// edge). uFadeMode 0 = near geometry (chunks) fades OUT toward uFadeEnd; 1 = horizon LOD fades IN. The discard
// is complementary (chunk where dither>=fade, LOD where dither<fade) so exactly one survives per pixel - no
// gaps, no double-draw. Outside the band the fade saturates to 0/1 so there is no dithering there.
uniform vec3 uCameraPos;
uniform float uFadeStart;   // band inner edge (RenderDistance - width); >= uFadeEnd disables the fade
uniform float uFadeEnd;     // band outer edge (= RenderDistance)
uniform int uFadeMode;

const float Bayer4[16] = float[16](
	 0.0/16.0,  8.0/16.0,  2.0/16.0, 10.0/16.0,
	12.0/16.0,  4.0/16.0, 14.0/16.0,  6.0/16.0,
	 3.0/16.0, 11.0/16.0,  1.0/16.0,  9.0/16.0,
	15.0/16.0,  7.0/16.0, 13.0/16.0,  5.0/16.0);

void LodCrossFade()
{
	float fade = clamp((distance(vWorldPos, uCameraPos) - uFadeStart) / max(uFadeEnd - uFadeStart, 0.001), 0.0, 1.0);
	ivec2 p = ivec2(gl_FragCoord.xy) & 3;
	float dither = Bayer4[p.y*4 + p.x];
	if (uFadeMode == 0) { if (dither <  fade) discard; }   // chunk: gone where dither below the fade level
	else                { if (dither >= fade) discard; }   // LOD:   shows where the chunk is gone
}

vec4 GetDiffuse()
{
	//If w value of normal is 1 dont apply shading (eg. bounding boxes)
	if (vNormal.w == 1) return vec4(vColor, 1);

	//Get color from the right texture array
	vec4 texColor = vec4(0);
	if(vTexCoord.w == 0) texColor = texture(uTextures16, vTexCoord.xyz);
	else if(vTexCoord.w == 1) texColor = texture(uTextures64, vTexCoord.xyz);
	else if(vTexCoord.w == 2) texColor = texture(uTextures256, vTexCoord.xyz);
	else if(vTexCoord.w == 3) texColor = texture(uTextures1024, vTexCoord.xyz);
	texColor.rgb *= vColor;

	// Anti-aliased alpha test for cutout foliage (leaves). The texture array is mipmapped (trilinear + 16x
	// aniso), so at distance the alpha channel is box-averaged down: a fixed 0.5 cutoff then shrinks leaf
	// coverage mip by mip, so leaves dissolve and the mip-transition band reads as a seam that crawls with the
	// camera. Sharpening the sampled alpha by its screen-space gradient (fwidth) restores a ~1px edge at every
	// mip, so leaf coverage stays put and the seam goes away (the median-preserving "anti-aliased alpha test").
	if(uCutoff)
	{
		float a = (texColor.a - 0.5) / max(fwidth(texColor.a), 0.0001) + 0.5;
		if(a < 0.5) discard;
	}

	return texColor;
}

vec4 EncodeNormal(vec4 normal)
{
	return normal*0.5 + 0.5;
}

void main()
{
	LodCrossFade();
	vec4 diffuse = GetDiffuse();

	outDiffuse = diffuse;
	outNormal = EncodeNormal(vNormal);
	outLight = vLight;
}

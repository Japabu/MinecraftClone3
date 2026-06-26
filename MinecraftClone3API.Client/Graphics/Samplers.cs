using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public static class Samplers
    {
        private static int _blockTexture;
        private static int _framebufferTexture;
        private static int _gui;
        private static int _celestial;

        public static void Load()
        {
            _blockTexture = GL.GenSampler();
            GL.SamplerParameter(_blockTexture, SamplerParameterName.TextureMinFilter, (float)TextureMinFilter.LinearMipmapLinear);
            GL.SamplerParameter(_blockTexture, SamplerParameterName.TextureMagFilter, (float)TextureMinFilter.Nearest);
            GL.SamplerParameter(_blockTexture, SamplerParameterName.TextureMaxAnisotropyExt, 16);

            _framebufferTexture = GL.GenSampler();
            // The G-buffer attachments have no mipmaps; leaving this sampler at the GL default
            // (NEAREST_MIPMAP_LINEAR) makes them texture-incomplete when sampled in the
            // composition pass, so it reads zero and the whole screen renders black.
            GL.SamplerParameter(_framebufferTexture, SamplerParameterName.TextureMinFilter, (float)TextureMinFilter.Nearest);
            GL.SamplerParameter(_framebufferTexture, SamplerParameterName.TextureMagFilter, (float)TextureMinFilter.Nearest);

            _gui = GL.GenSampler();
            GL.SamplerParameter(_gui, SamplerParameterName.TextureMinFilter, (float)TextureMinFilter.Nearest);
            GL.SamplerParameter(_gui, SamplerParameterName.TextureMagFilter, (float)TextureMinFilter.Nearest);

            // Sun/moon billboards are small on screen, so sampling the auto-generated mipmaps (the GL default
            // sampler 0's NEAREST_MIPMAP_LINEAR) averaged the whole disc into a dim blob and REPEAT wrap let the
            // edge bleed. Nearest filtering with no mipmaps and a clamped edge keeps the pixel-art sun/moon crisp.
            _celestial = GL.GenSampler();
            GL.SamplerParameter(_celestial, SamplerParameterName.TextureMinFilter, (float)TextureMinFilter.Nearest);
            GL.SamplerParameter(_celestial, SamplerParameterName.TextureMagFilter, (float)TextureMinFilter.Nearest);
            GL.SamplerParameter(_celestial, SamplerParameterName.TextureWrapS, (float)TextureWrapMode.ClampToEdge);
            GL.SamplerParameter(_celestial, SamplerParameterName.TextureWrapT, (float)TextureWrapMode.ClampToEdge);
        }

        public static void BindBlockTextureSampler()
        {
            for (var x = 0; x < BlockTextureManager.Sizes.Length; x++)
                GL.BindSampler(x, _blockTexture);
        }

        public static void BindFramebufferTextureSampler(int unit) => GL.BindSampler(unit, _framebufferTexture);

        public static void BindGuiSampler(int unit) => GL.BindSampler(unit, _gui);

        public static void BindCelestialSampler(int unit) => GL.BindSampler(unit, _celestial);
    }
}

using MinecraftClone3API.Graphics;

namespace MinecraftClone3API.IO
{
    /// <summary>Client-only resource readers that produce live GPU objects. Kept out of Core's
    /// <see cref="ResourceReader"/> (which stays GPU-free) so the headless server never links them.</summary>
    public static class GlResources
    {
        public static Texture ReadTexture(string path) => new Texture(ResourceReader.ReadTextureData(path));
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Text;
using MinecraftClone3API.Client;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using StbImageSharp;

namespace MinecraftClone3API.IO
{
    public static class ResourceReader
    {
        private static Dictionary<string, BlockTexture> _cachedTextures;
        private static Dictionary<string, BlockModel> _cachedModels;

        public static void ClearCache()
        {
            _cachedModels = new Dictionary<string, BlockModel>();
            _cachedTextures = new Dictionary<string, BlockTexture>();
        }

        public static byte[] ReadBytes(string path) => ResourceManager.LoadAsset(path);

        public static bool Exists(string path) => ResourceManager.ExistsAsset(path);

        public static string ReadString(string path) => Encoding.Default.GetString(ReadBytes(path));

        public static TextureData ReadTextureData(string path)
        {
            var image = ImageResult.FromMemory(ReadBytes(path), ColorComponents.RedGreenBlueAlpha);
            return new TextureData(image.Data, image.Width, image.Height);
        }

        public static Texture ReadTexture(string path)
            => new Texture(ReadTextureData(path));

        public static BlockTexture ReadBlockTexture(string path)
        {
            var resolved = BlockModel.GetRelativePaths(path, path, ".png").FirstOrDefault(Exists);
            if (resolved == null)
            {
                Logger.Error($"Texture \"{path}\" could not be found!");
                return ClientResources.MissingTexture;
            }

            if (_cachedTextures.TryGetValue(resolved, out var tex)) return tex;
            tex = BlockTextureManager.LoadTexture(ReadTextureData(resolved));
            _cachedTextures.Add(resolved, tex);
            return tex;
        }

        public static Shader ReadShader(string path) => new Shader(path);

        public static BlockModel ReadBlockModel(string path)
        {
            var resolved = BlockModel.GetRelativePaths(path, path, ".json").FirstOrDefault(Exists);
            if (resolved == null)
            {
                Logger.Error($"Block model \"{path}\" could not be found!");
                return ClientResources.MissingModel;
            }

            if (_cachedModels.TryGetValue(resolved, out var model)) return model;
            model = BlockModel.Parse(ReadString(resolved), resolved);
            _cachedModels.Add(resolved, model);
            return model;
        }
    }
}

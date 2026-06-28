using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;
using Newtonsoft.Json.Linq;
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

        public static BlockTexture ReadBlockTexture(string path)
        {
            var resolved = BlockModel.GetRelativePaths(path, path, ".png").FirstOrDefault(Exists);
            if (resolved == null)
            {
                Logger.Error($"Texture \"{path}\" could not be found!");
                return CommonResources.MissingTexture;
            }

            if (_cachedTextures.TryGetValue(resolved, out var tex)) return tex;

            var data = ReadTextureData(resolved);
            // A vertical strip whose height is a whole multiple of its width is a Minecraft animation
            // sheet (water_still, lava, fire, …). Slice it into square frames; faces bake frame 0 and the
            // client's BlockAnimator cycles the strip (see BlockTextureManager.LoadAnimatedTexture).
            if (data.Width > 0 && data.Height > data.Width && data.Height % data.Width == 0)
                tex = BlockTextureManager.LoadAnimatedTexture(data, data.Height / data.Width, ReadFrameTime(resolved));
            else
                tex = BlockTextureManager.LoadTexture(data);

            _cachedTextures.Add(resolved, tex);
            return tex;
        }

        private static int ReadFrameTime(string texturePath)
        {
            var metaPath = texturePath + ".mcmeta";
            if (!Exists(metaPath)) return 1;

            try
            {
                var frameTime = JObject.Parse(ReadString(metaPath))["animation"]?["frametime"]?.Value<int>() ?? 1;
                return frameTime <= 0 ? 1 : frameTime;
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not parse animation metadata \"{metaPath}\"");
                Logger.Exception(e);
                return 1;
            }
        }

        public static BlockModel ReadBlockModel(string path)
        {
            var resolved = BlockModel.GetRelativePaths(path, path, ".json").FirstOrDefault(Exists);
            if (resolved == null)
            {
                Logger.Error($"Block model \"{path}\" could not be found!");
                return CommonResources.MissingModel;
            }

            if (_cachedModels.TryGetValue(resolved, out var model)) return model;
            model = BlockModel.Parse(ReadString(resolved), resolved);
            _cachedModels.Add(resolved, model);
            return model;
        }

        /// <summary>The parsed blockstate definition for a content id (e.g. <c>"minecraft:furnace"</c>) from the
        /// pack's <c>&lt;ns&gt;/blockstates/&lt;name&gt;.json</c>, or null if the pack has no such file (the block
        /// then falls back to its single model).</summary>
        public static BlockStateDefinition ReadBlockState(string id)
        {
            var ns = "minecraft";
            var name = id;
            var colon = id.IndexOf(':');
            if (colon >= 0)
            {
                ns = id.Substring(0, colon);
                name = id.Substring(colon + 1);
            }

            var path = $"{ns}/blockstates/{name}.json";
            return Exists(path) ? BlockStateDefinition.Parse(ReadString(path), path) : null;
        }
    }
}

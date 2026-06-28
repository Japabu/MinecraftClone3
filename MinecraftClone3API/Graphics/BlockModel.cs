using System;
using System.Collections.Generic;
using System.Linq;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Silk.NET.Maths;
// ReSharper disable InconsistentNaming

namespace MinecraftClone3API.Graphics
{
    public class BlockModel
    {
        private const string JsonExtension = ".json";
        private const string PngExtension = ".png";
        private const string SystemRoot = "System/";

        private const float OneOverSixteen = 1/16f;

        public static BlockModel Parse(string source, string path)
        {
            BlockModelParent m;
            try
            {
                m = JsonConvert.DeserializeObject<BlockModelParent>(source);
            }
            catch (Exception e)
            {
                Logger.Error($"Error loading block model \"{path}\"");
                Logger.Exception(e);
                throw;
            }

            var sources = new List<string>();
            var currentPath = path;

            while (!string.IsNullOrEmpty(m.Parent))
            {
                if (sources.Count > 100)
                    throw new Exception($"\"{m.Parent}\" has either more than 100 parents or is an endless loop!");

                
                
                var filename = GetRelativePaths(currentPath, m.Parent, JsonExtension).FirstOrDefault(ResourceReader.Exists);

                if (filename == null)
                {
                    Logger.Error($"{m.Parent} could not be found in {currentPath}!");
                    m.Parent = null;
                    continue;
                }

                currentPath = filename;

                //Find first existing source, add it to the sources and find its parent
                var parentSource = ResourceReader.ReadString(filename);
                try
                {
                    m = JsonConvert.DeserializeObject<BlockModelParent>(parentSource);
                }
                catch (Exception e)
                {
                    Logger.Error($"Error loading block model \"{filename}\"");
                    Logger.Exception(e);
                    throw;
                }
                sources.Add(parentSource);
            }

            sources.Reverse();
            sources.Add(source);
            var model = new BlockModel();
            try
            {
                sources.ForEach(s => JsonConvert.PopulateObject(s, model));
            }
            catch (Exception e)
            {
                Logger.Error($"Error populating block model \"{path}\"");
                Logger.Exception(e);
                throw;
            }

            //Dont load textures if there arent any
            if(model.Textures == null) Logger.Error($"\"{path}\" does not contain any texture definitions!");
            else LoadModelTextures(model, path);

            return model;
        }

        private static void LoadModelTextures(BlockModel model, string path)
        {
            var loadedTextures = new Dictionary<string, BlockTexture>();
            var variableFound = true;
            var loadedSomething = true;
            var counter = 0;
            while (variableFound && loadedSomething)
            {
                variableFound = false;
                loadedSomething = false;
                foreach (var entry in model.Textures)
                {
                    //If texture is already loaded continue
                    if (loadedTextures.ContainsKey(entry.Key)) continue;

                    if (!entry.Value.StartsWith("#")) //If not a variable load texture
                    {
                        var filename = GetRelativePaths(path, entry.Value, PngExtension).FirstOrDefault(ResourceReader.Exists);

                        if (filename == null)
                        {
                            Logger.Error($"Texture \"{entry.Value}\" could not be found in {path}!");
                            loadedTextures.Add(entry.Key, CommonResources.MissingTexture);
                            continue;
                        }

                        loadedTextures.Add(entry.Key, ResourceReader.ReadBlockTexture(filename));

                        loadedSomething = true;
                    }
                    else //If variable try to find its value
                    {
                        if (loadedTextures.TryGetValue(entry.Value.Substring(1), out var texture))
                        {
                            loadedTextures.Add(entry.Key, texture);
                            // Count a resolved variable as progress, so multi-level chains (a furnace's
                            // down -> #bottom -> #top -> texture) keep iterating instead of stopping a pass
                            // early once all the real texture files are loaded.
                            loadedSomething = true;
                        }
                        else
                            variableFound = true;
                    }
                }

                counter++;

                if (counter >= 100)
                    throw new Exception($"\"{path}\" has either more than 100 texture parents or is an endless loop!");
            }

            foreach (var element in model.Elements)
                foreach (var entry in element.Faces)
                {
                    if (loadedTextures.TryGetValue(entry.Value.Texture.Substring(1), out var texture))
                        entry.Value.LoadedTexture = texture;
                    else throw new Exception($"Texture variable \"{entry.Value.Texture}\" in \"{path}\" does not have a value!");
                }
        }

        internal static List<string> GetRelativePaths(string root, string path, string extension)
        {
            //Find parent file relatively
            var paths = new List<string> {path, path + extension};

            var i = root.LastIndexOf("/", StringComparison.Ordinal) + 1;
            paths.Add(root.Substring(0, i) + path);
            paths.Add(root.Substring(0, i) + path + extension);

            i = root.IndexOf("/", StringComparison.Ordinal) + 1;
            paths.Add(root.Substring(0, i) + path);
            paths.Add(root.Substring(0, i) + path + extension);

            paths.Add(SystemRoot + path);
            paths.Add(SystemRoot + path + extension);

            //Minecraft resource location ([ns:]category/path), e.g. "minecraft:block/cube_all" -> "minecraft/models/block/cube_all.json"
            var category = extension == JsonExtension ? "models" : extension == PngExtension ? "textures" : null;
            if (category != null)
            {
                var ns = "minecraft";
                var loc = path;
                var colon = path.IndexOf(':');
                if (colon >= 0)
                {
                    ns = path.Substring(0, colon);
                    loc = path.Substring(colon + 1);
                }

                paths.Add($"{ns}/{category}/{loc}{extension}");
            }

            return paths;
        }

        private class BlockModelParent
        {
            public string Parent;
        }

        public class DisplayEntry
        {
            public Vector3D<float> Rotation;
            public Vector3D<float> Translation;
            public Vector3D<float> Scale;
        }

        public class Element
        {
            public Vector3D<float> From;
            public Vector3D<float> To;
            public Dictionary<BlockFace, FaceData> Faces;
        }

        public class FaceData
        {
            public Vector4D<float> UV = new Vector4D<float>(0, 0, 16, 16);
            public string Texture;
            public BlockFace Cullface;
            public int TintIndex = -1;

            [JsonIgnore]
            public BlockTexture LoadedTexture = CommonResources.MissingTexture;

            [JsonIgnore]
            private Vector2D<float>[] _texCoords;

            /// <summary>The four corner UVs, built once and reused: UV is fixed after model parsing,
            /// so re-deriving the array per face was pure per-remesh allocation on the mesh thread.</summary>
            public Vector2D<float>[] GetTexCoords()
            {
                return _texCoords ??= new[]
                {
                    new Vector2D<float>(UV[0], UV[1])*OneOverSixteen, new Vector2D<float>(UV[2], UV[1])*OneOverSixteen,
                    new Vector2D<float>(UV[0], UV[3])*OneOverSixteen, new Vector2D<float>(UV[2], UV[3])*OneOverSixteen
                };
            }
        }
        
        public string Parent;
        public bool AmbientOcclusion = true;
        public Dictionary<string, DisplayEntry> Display;
        [JsonConverter(typeof(TextureMapConverter))]
        public Dictionary<string, string> Textures;
        public Element[] Elements;

        public BlockModel()
        {
            //Default json constructor
        }

        /// <summary>A texture entry is either a plain sprite path (<c>"all": "minecraft:block/stone"</c>) or,
        /// since the 26.x format, an object carrying the path under <c>sprite</c> plus flags
        /// (<c>"all": { "sprite": "...", "force_translucent": true }</c>). We only need the path — the engine
        /// drives translucency off the block's <see cref="Block.IsTransparent"/>, not the model flag — so both
        /// forms collapse to the sprite string and variable references (<c>"#all"</c>) pass through unchanged.</summary>
        private class TextureMapConverter : JsonConverter<Dictionary<string, string>>
        {
            public override Dictionary<string, string> ReadJson(JsonReader reader, System.Type objectType,
                Dictionary<string, string> existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                var result = existingValue ?? new Dictionary<string, string>();
                foreach (var prop in JObject.Load(reader).Properties())
                {
                    var value = prop.Value;
                    result[prop.Name] = value.Type == JTokenType.Object
                        ? value["sprite"]?.Value<string>()
                        : value.Value<string>();
                }
                return result;
            }

            public override void WriteJson(JsonWriter writer, Dictionary<string, string> value, JsonSerializer serializer)
                => throw new System.NotSupportedException();
        }
    }
}

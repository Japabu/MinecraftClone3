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

            FillMissingUvs(model);

            //Dont load textures if there arent any
            if(model.Textures == null) Logger.Error($"\"{path}\" does not contain any texture definitions!");
            else LoadModelTextures(model, path);

            return model;
        }

        // Minecraft auto-generates a face's UV from the element's from/to when the model omits "uv"; without this
        // the engine's full-texture default stretches the texture across any partial face (a wall arm, a fence
        // side). Full-cube faces reduce to [0,0,16,16] (unchanged), and faces with an explicit uv keep it.
        private static void FillMissingUvs(BlockModel model)
        {
            if (model.Elements == null) return;
            foreach (var element in model.Elements)
            {
                if (element.Faces == null) continue;
                foreach (var entry in element.Faces)
                    if (!entry.Value.HasExplicitUv)
                        entry.Value.UV = AutoUv(entry.Key, element.From, element.To);
            }
        }

        // The default face UV (0..16 texture space) Minecraft derives from the element's from/to: each face takes
        // the two perpendicular axes of its extent, with the V-axis (and the back/east faces' U) flipped to match
        // the texture orientation. Verified to reproduce vanilla's explicit wall/fence UVs and to give
        // [0,0,16,16] for a full cube. Engine axes: North=-Z(Back), South=+Z(Front), East=+X(Right), West=-X(Left).
        private static Vector4D<float> AutoUv(BlockFace face, Vector3D<float> from, Vector3D<float> to)
        {
            switch (face)
            {
                case BlockFace.Top:    return new Vector4D<float>(from.X, from.Z, to.X, to.Z);
                case BlockFace.Bottom: return new Vector4D<float>(from.X, 16 - to.Z, to.X, 16 - from.Z);
                case BlockFace.Back:   return new Vector4D<float>(16 - to.X, 16 - to.Y, 16 - from.X, 16 - from.Y);
                case BlockFace.Front:  return new Vector4D<float>(from.X, 16 - to.Y, to.X, 16 - from.Y);
                case BlockFace.Left:   return new Vector4D<float>(from.Z, 16 - to.Y, to.Z, 16 - from.Y);
                case BlockFace.Right:  return new Vector4D<float>(16 - to.Z, 16 - to.Y, 16 - from.Z, 16 - from.Y);
                default:               return new Vector4D<float>(0, 0, 16, 16);
            }
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
            public ElementRotation Rotation;
            public Dictionary<BlockFace, FaceData> Faces;
        }

        /// <summary>A model element's optional rotation about an <see cref="Origin"/> (Minecraft 0..16 model
        /// space) by <see cref="Angle"/> degrees around one <see cref="Axis"/>. <see cref="Rescale"/> scales the
        /// two perpendicular axes by 1/cos(angle) so a 45° plane spans the full block — this is what gives the
        /// <c>cross</c> models (flowers, grass, saplings) their X silhouette. Applied by the mesher at the
        /// element's placement, before the block-state orientation.</summary>
        public class ElementRotation
        {
            public Vector3D<float> Origin;
            public string Axis;
            public float Angle;
            public bool Rescale;
        }

        public class FaceData
        {
            // NaN in X marks "uv omitted in the model" (real uvs are 0..16, so NaN can't collide). The parser
            // then fills it from the element's from/to — Minecraft's auto-UV — instead of stretching the full
            // texture across a partial face. See BlockModel.FillMissingUvs.
            public Vector4D<float> UV = new Vector4D<float>(float.NaN, 0, 0, 0);
            public string Texture;
            public BlockFace Cullface;
            public int TintIndex = -1;

            public bool HasExplicitUv => !float.IsNaN(UV.X);

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

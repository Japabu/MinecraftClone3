using System;
using System.Collections.Generic;
using System.Linq;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Client;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Newtonsoft.Json;
using OpenTK.Mathematics;
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
                            loadedTextures.Add(entry.Key, ClientResources.MissingTexture);
                            continue;
                        }

                        loadedTextures.Add(entry.Key, ResourceReader.ReadBlockTexture(filename));

                        loadedSomething = true;
                    }
                    else //If variable try to find its value
                    {
                        if (loadedTextures.TryGetValue(entry.Value.Substring(1), out var texture))
                            loadedTextures.Add(entry.Key, texture);

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

        private static List<string> GetRelativePaths(string root, string path, string extension)
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

            return paths;
        }

        private class BlockModelParent
        {
            public string Parent;
        }

        public class DisplayEntry
        {
            public Vector3 Rotation;
            public Vector3 Translation;
            public Vector3 Scale;
        }

        public class Element
        {
            public Vector3 From;
            public Vector3 To;
            public Dictionary<BlockFace, FaceData> Faces;
        }

        public class FaceData
        {
            public Vector4 UV = new Vector4(0, 0, 16, 16);
            public string Texture;
            public BlockFace Cullface;
            public int TintIndex = -1;

            [JsonIgnore]
            public BlockTexture LoadedTexture = ClientResources.MissingTexture;

            public Vector2[] GetTexCoords()
            {
                return new[]
                {
                    new Vector2(UV[0], UV[1])*OneOverSixteen, new Vector2(UV[2], UV[1])*OneOverSixteen,
                    new Vector2(UV[0], UV[3])*OneOverSixteen, new Vector2(UV[2], UV[3])*OneOverSixteen
                };
            }
        }
        
        public string Parent;
        public bool AmbientOcclusion = true;
        public Dictionary<string, DisplayEntry> Display;
        public Dictionary<string, string> Textures;
        public Element[] Elements;

        public BlockModel()
        {
            //Default json constructor
        }
    }
}

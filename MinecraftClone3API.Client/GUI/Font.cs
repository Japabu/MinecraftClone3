using System;
using System.Collections.Generic;
using MinecraftClone3API.Client.Graphics;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Newtonsoft.Json;
using Silk.NET.Maths;

namespace MinecraftClone3API.Client.GUI
{
    /// <summary>
    /// Renders text through Minecraft's modern font-provider format
    /// (assets/minecraft/font/default.json). Resolves <c>reference</c> providers (which the current
    /// default font is built entirely from) and loads the <c>bitmap</c> and <c>space</c> providers
    /// they pull in. Glyph widths are derived by alpha-scanning each cell, normalised to an 8px em.
    /// </summary>
    public static class Font
    {
        private const int Em = 8;
        private const string DefinitionPath = "minecraft/font/default.json";

        private struct Glyph
        {
            public Texture Texture;
            public Rectangle Source;
            public float Width;
            public int Advance;
        }

        private class FontDefinition
        {
            public List<Provider> Providers { get; set; }
        }

        private class Provider
        {
            public string Type { get; set; }
            public string File { get; set; }
            public string Id { get; set; }
            public string[] Chars { get; set; }
            public Dictionary<string, float> Advances { get; set; }
        }

        private static readonly Dictionary<int, Glyph> Glyphs = new Dictionary<int, Glyph>();
        private static bool _loaded;

        public static int LineHeight(int scale = 2) => Em * scale;

        public static void Load()
        {
            Glyphs.Clear();
            _loaded = false;

            if (!ResourceReader.Exists(DefinitionPath))
            {
                Logger.Error($"Font definition \"{DefinitionPath}\" not found; text rendering disabled.");
                return;
            }

            LoadDefinition(DefinitionPath, new HashSet<string>());

            _loaded = Glyphs.Count > 0;
        }

        /// <summary>
        /// Loads every provider in the font definition at <paramref name="path"/>. <c>reference</c>
        /// providers are resolved to the definition they name and loaded recursively; <paramref name="visited"/>
        /// guards against reference cycles.
        /// </summary>
        private static void LoadDefinition(string path, HashSet<string> visited)
        {
            if (!visited.Add(path)) return;

            if (!ResourceReader.Exists(path))
            {
                Logger.Error($"Font definition \"{path}\" not found.");
                return;
            }

            FontDefinition definition;
            try
            {
                definition = JsonConvert.DeserializeObject<FontDefinition>(ResourceReader.ReadString(path));
            }
            catch (Exception e)
            {
                Logger.Error($"Error loading font definition \"{path}\"");
                Logger.Exception(e);
                return;
            }

            if (definition?.Providers == null)
            {
                Logger.Error($"Font definition \"{path}\" has no providers.");
                return;
            }

            foreach (var provider in definition.Providers)
            {
                switch (provider.Type)
                {
                    case "bitmap":
                        LoadBitmapProvider(provider);
                        break;
                    case "space":
                        LoadSpaceProvider(provider);
                        break;
                    case "reference":
                        if (provider.Id != null)
                            LoadDefinition(ResolveDefinitionPath(provider.Id), visited);
                        break;
                }
            }
        }

        /// <summary>Resolves a font id (e.g. <c>minecraft:include/default</c>) to its asset path.</summary>
        private static string ResolveDefinitionPath(string id)
        {
            var ns = "minecraft";
            var colon = id.IndexOf(':');
            if (colon >= 0)
            {
                ns = id.Substring(0, colon);
                id = id.Substring(colon + 1);
            }
            return $"{ns}/font/{id}.json";
        }

        public static int MeasureWidth(string text, int scale = 2)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            var width = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var codepoint = NextCodepoint(text, ref i);
                width += (Glyphs.TryGetValue(codepoint, out var glyph) ? glyph.Advance : SpaceAdvance) * scale;
            }
            return width;
        }

        public static void DrawString(string text, int x, int y, int scale = 2, Vector4D<float>? color = null, bool shadow = true)
        {
            if (!_loaded || string.IsNullOrEmpty(text)) return;

            var tint = color ?? new Vector4D<float>(1f,1f,1f,1f);
            if (shadow)
                DrawRun(text, x + scale, y + scale, scale, new Vector4D<float>(0.25f, 0.25f, 0.25f, tint.W));
            DrawRun(text, x, y, scale, tint);
        }

        private static void DrawRun(string text, int x, int y, int scale, Vector4D<float> color)
        {
            var pen = x;
            for (var i = 0; i < text.Length; i++)
            {
                var codepoint = NextCodepoint(text, ref i);
                if (!Glyphs.TryGetValue(codepoint, out var glyph))
                {
                    pen += SpaceAdvance * scale;
                    continue;
                }

                if (glyph.Texture != null && glyph.Width > 0)
                {
                    var w = (int)(glyph.Width * scale + 0.5f);
                    var destination = new Rectangle(pen, y, pen + w, y + Em * scale);
                    GuiRenderer.DrawTexture(glyph.Texture, destination, glyph.Source, color);
                }

                pen += glyph.Advance * scale;
            }
        }

        private static void LoadBitmapProvider(Provider provider)
        {
            if (provider.File == null || provider.Chars == null || provider.Chars.Length == 0) return;

            var path = ResolveTexturePath(provider.File);
            if (!ResourceReader.Exists(path))
            {
                Logger.Error($"Font texture \"{path}\" not found; skipping provider.");
                return;
            }

            var data = ResourceReader.ReadTextureData(path);
            var texture = new Texture(data);

            var rows = provider.Chars.Length;
            var cols = CountCodepoints(provider.Chars[0]);
            if (cols == 0) return;

            var cellW = data.Width / cols;
            var cellH = data.Height / rows;

            for (var row = 0; row < rows; row++)
            {
                var col = 0;
                foreach (var codepoint in Codepoints(provider.Chars[row]))
                {
                    if (col >= cols) break;
                    var cellX = col * cellW;
                    var cellY = row * cellH;
                    col++;

                    if (codepoint == 0 || Glyphs.ContainsKey(codepoint)) continue;

                    var pixelWidth = MeasureGlyphPixelWidth(data, cellX, cellY, cellW, cellH);
                    var emWidth = pixelWidth * (float)Em / cellW;

                    Glyphs[codepoint] = new Glyph
                    {
                        Texture = texture,
                        Source = new Rectangle(cellX, cellY, cellX + pixelWidth, cellY + cellH),
                        Width = emWidth,
                        Advance = codepoint == ' ' ? 4 : (int)(emWidth + 0.5f) + 1
                    };
                }
            }
        }

        private static void LoadSpaceProvider(Provider provider)
        {
            if (provider.Advances == null) return;

            foreach (var entry in provider.Advances)
                foreach (var codepoint in Codepoints(entry.Key))
                {
                    if (Glyphs.ContainsKey(codepoint)) continue;
                    Glyphs[codepoint] = new Glyph {Advance = (int)(entry.Value + 0.5f)};
                }
        }

        private static int MeasureGlyphPixelWidth(TextureData data, int cellX, int cellY, int cellW, int cellH)
        {
            for (var x = cellW - 1; x >= 0; x--)
                for (var y = 0; y < cellH; y++)
                    if (data.Pixels[((cellY + y) * data.Width + cellX + x) * 4 + 3] != 0)
                        return x + 1;
            return 0;
        }

        private static int SpaceAdvance => Glyphs.TryGetValue(' ', out var glyph) ? glyph.Advance : 4;

        private static string ResolveTexturePath(string file)
        {
            var ns = "minecraft";
            var colon = file.IndexOf(':');
            if (colon >= 0)
            {
                ns = file.Substring(0, colon);
                file = file.Substring(colon + 1);
            }
            return $"{ns}/textures/{file}";
        }

        /// <summary>
        /// Decodes the codepoint at <paramref name="i"/>, advancing <paramref name="i"/> past the low
        /// surrogate of a pair. Lets the hot measure/draw loops iterate codepoints without allocating an
        /// enumerator (the per-frame <c>Codepoints</c> iterator was a top main-thread allocator).
        /// </summary>
        private static int NextCodepoint(string text, ref int i)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var codepoint = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
                return codepoint;
            }
            return text[i];
        }

        private static int CountCodepoints(string text)
        {
            var count = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
                count++;
            }
            return count;
        }

        private static IEnumerable<int> Codepoints(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    yield return char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                else yield return text[i];
            }
        }
    }
}

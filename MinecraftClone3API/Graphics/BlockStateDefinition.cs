using System;
using System.Collections.Generic;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Newtonsoft.Json.Linq;
using Silk.NET.Maths;

namespace MinecraftClone3API.Graphics
{
    /// <summary>One resolved blockstate variant: the model to render plus the rotation the blockstate file
    /// applies to it (<c>x</c>/<c>y</c> in degrees, baked into <see cref="Rotation"/>).</summary>
    public sealed class BlockStateVariant
    {
        public BlockModel Model;
        public Matrix4X4<float> Rotation;
    }

    /// <summary>
    /// A parsed Minecraft <c>blockstates/&lt;name&gt;.json</c> (the <c>variants</c> form): a list of
    /// (property conditions → variant) entries. <see cref="Resolve"/> picks the first variant whose conditions
    /// are all satisfied by a block's current state (e.g. <c>facing=east, lit=true</c>), so the engine drives a
    /// block's facing/lit appearance straight from the resource pack rather than from per-block code. The
    /// <c>multipart</c> form is not yet supported (such files resolve to null, falling back to the plain model).
    /// </summary>
    public sealed class BlockStateDefinition
    {
        private readonly List<(KeyValuePair<string, string>[] conditions, BlockStateVariant variant)> _variants;

        private BlockStateDefinition(List<(KeyValuePair<string, string>[], BlockStateVariant)> variants)
        {
            _variants = variants;
        }

        /// <summary>The first variant whose every <c>property=value</c> condition is present and equal in
        /// <paramref name="state"/>, or null if none match. An unconditional (<c>""</c>) variant matches any
        /// state.</summary>
        public BlockStateVariant Resolve(IReadOnlyDictionary<string, string> state)
        {
            foreach (var (conditions, variant) in _variants)
            {
                var ok = true;
                foreach (var c in conditions)
                    if (state == null || !state.TryGetValue(c.Key, out var v) || v != c.Value)
                    {
                        ok = false;
                        break;
                    }

                if (ok) return variant;
            }

            return null;
        }

        public static BlockStateDefinition Parse(string source, string path)
        {
            JObject json;
            try
            {
                json = JObject.Parse(source);
            }
            catch (Exception e)
            {
                Logger.Error($"Error loading blockstate \"{path}\"");
                Logger.Exception(e);
                return null;
            }

            if (!(json["variants"] is JObject variants)) return null; // multipart not supported

            var list = new List<(KeyValuePair<string, string>[], BlockStateVariant)>();
            foreach (var entry in variants)
            {
                // A variant value is either a single model spec or a weighted array (take the first).
                var spec = entry.Value is JArray array ? (array.Count > 0 ? array[0] : null) : entry.Value;
                if (!(spec is JObject obj)) continue;

                var modelId = (string) obj["model"];
                if (string.IsNullOrEmpty(modelId)) continue;

                var x = obj["x"] != null ? obj["x"].Value<int>() : 0;
                var y = obj["y"] != null ? obj["y"].Value<int>() : 0;

                var variant = new BlockStateVariant
                {
                    Model = ResourceReader.ReadBlockModel(modelId),
                    Rotation = RotationFor(x, y)
                };

                list.Add((ParseConditions(entry.Key), variant));
            }

            return list.Count > 0 ? new BlockStateDefinition(list) : null;
        }

        private static KeyValuePair<string, string>[] ParseConditions(string key)
        {
            if (string.IsNullOrEmpty(key)) return Array.Empty<KeyValuePair<string, string>>();

            var parts = key.Split(',');
            var conditions = new List<KeyValuePair<string, string>>(parts.Length);
            foreach (var part in parts)
            {
                var eq = part.IndexOf('=');
                if (eq < 0) continue;
                conditions.Add(new KeyValuePair<string, string>(part.Substring(0, eq), part.Substring(eq + 1)));
            }

            return conditions.ToArray();
        }

        // Blockstate x/y are degrees (multiples of 90) rotating the model about its centre — x then y. The
        // engine centres elements (-0.5..0.5) before the orient transform, so these are plain centre rotations.
        // The signs are negated to match the engine's axes (+X east, +Z south): e.g. the furnace's north-facing
        // model with y=90 then points east, as the pack intends.
        private static Matrix4X4<float> RotationFor(int x, int y) =>
            Matrix4X4.CreateRotationX(-(x * (MathF.PI / 180f))) *
            Matrix4X4.CreateRotationY(-(y * (MathF.PI / 180f)));
    }
}

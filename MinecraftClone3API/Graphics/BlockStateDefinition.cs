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

        /// <summary>The blockstate <c>uvlock</c> flag, and the y-rotation (degrees, normalized 0/90/180/270) it
        /// applies — the mesher uses these to counter-rotate the UVs of axis-aligned (top/bottom) faces so a
        /// rotated arm (fence/wall side) keeps its texture world-aligned instead of spun with the model.</summary>
        public bool UvLock;
        public int YRot;
    }

    /// <summary>
    /// A parsed Minecraft <c>blockstates/&lt;name&gt;.json</c> in either form:
    /// <list type="bullet">
    /// <item><b>variants</b> — a list of (property conditions → variant); <see cref="Resolve"/> picks the first
    /// variant whose conditions all hold for a block's current state (a furnace's facing/lit, a slab's half).</item>
    /// <item><b>multipart</b> — a list of (condition → applied model); <see cref="ResolveAll"/> returns
    /// <em>every</em> case whose <c>when</c> matches, composing them (a fence post plus one side arm per
    /// connected neighbour). <see cref="IsMultipart"/> is true.</item>
    /// </list>
    /// </summary>
    public sealed class BlockStateDefinition
    {
        private readonly List<(KeyValuePair<string, string>[] conditions, BlockStateVariant variant)> _variants;
        private readonly List<MultipartCase> _multipart;

        private BlockStateDefinition(List<(KeyValuePair<string, string>[], BlockStateVariant)> variants)
        {
            _variants = variants;
        }

        private BlockStateDefinition(List<MultipartCase> multipart)
        {
            _multipart = multipart;
        }

        /// <summary>True for the <c>multipart</c> form — the mesher must emit every model from
        /// <see cref="ResolveAll"/> rather than a single <see cref="Resolve"/> variant.</summary>
        public bool IsMultipart => _multipart != null;

        /// <summary>The first <b>variants</b>-form variant whose every <c>property=value</c> condition is present
        /// and equal in <paramref name="state"/>, or null (also null for a multipart definition). An
        /// unconditional (<c>""</c>) variant matches any state.</summary>
        public BlockStateVariant Resolve(IReadOnlyDictionary<string, string> state)
        {
            if (_variants == null) return null;
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

        /// <summary>Every variant to render for <paramref name="state"/>: for the multipart form, all cases whose
        /// <c>when</c> matches (composed); for the variants form, the single <see cref="Resolve"/> match (0 or 1).</summary>
        public List<BlockStateVariant> ResolveAll(IReadOnlyDictionary<string, string> state)
        {
            var result = new List<BlockStateVariant>();
            if (_multipart != null)
            {
                foreach (var c in _multipart)
                    if (c.Matches(state))
                        result.Add(c.Variant);
            }
            else
            {
                var v = Resolve(state);
                if (v != null) result.Add(v);
            }

            return result;
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

            if (json["multipart"] is JArray multipart) return ParseMultipart(multipart);
            if (!(json["variants"] is JObject variants)) return null;

            var list = new List<(KeyValuePair<string, string>[], BlockStateVariant)>();
            foreach (var entry in variants)
            {
                // A variant value is either a single model spec or a weighted array (take the first).
                var spec = entry.Value is JArray array ? (array.Count > 0 ? array[0] : null) : entry.Value;
                if (!(spec is JObject obj)) continue;

                var variant = ParseVariant(obj);
                if (variant == null) continue;

                list.Add((ParseConditions(entry.Key), variant));
            }

            return list.Count > 0 ? new BlockStateDefinition(list) : null;
        }

        private static BlockStateDefinition ParseMultipart(JArray multipart)
        {
            var cases = new List<MultipartCase>();
            foreach (var token in multipart)
            {
                if (!(token is JObject caseObj)) continue;

                // apply is a single model spec or a weighted array (take the first).
                var applyToken = caseObj["apply"];
                var spec = applyToken is JArray array ? (array.Count > 0 ? array[0] : null) : applyToken;
                if (!(spec is JObject applyObj)) continue;

                var variant = ParseVariant(applyObj);
                if (variant == null) continue;

                cases.Add(new MultipartCase
                {
                    AnyOf = ParseWhen(caseObj["when"] as JObject),
                    Variant = variant
                });
            }

            return cases.Count > 0 ? new BlockStateDefinition(cases) : null;
        }

        private static BlockStateVariant ParseVariant(JObject obj)
        {
            var modelId = (string) obj["model"];
            if (string.IsNullOrEmpty(modelId)) return null;

            var x = obj["x"] != null ? obj["x"].Value<int>() : 0;
            var y = obj["y"] != null ? obj["y"].Value<int>() : 0;
            return new BlockStateVariant
            {
                Model = ResourceReader.ReadBlockModel(modelId),
                Rotation = RotationFor(x, y),
                UvLock = obj["uvlock"] != null && obj["uvlock"].Value<bool>(),
                YRot = ((y % 360) + 360) % 360
            };
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

        // A multipart `when`: null (always apply), a single AND-group of property->acceptable-values, or an `OR`
        // of such groups. Returned as a disjunction of conjunctions (the outer array is the OR).
        private static KeyValuePair<string, string[]>[][] ParseWhen(JObject when)
        {
            if (when == null) return null;

            if (when["OR"] is JArray or)
            {
                var groups = new List<KeyValuePair<string, string[]>[]>();
                foreach (var sub in or)
                    if (sub is JObject o) groups.Add(ParseGroup(o));
                return groups.Count > 0 ? groups.ToArray() : null;
            }

            return new[] { ParseGroup(when) };
        }

        private static KeyValuePair<string, string[]>[] ParseGroup(JObject obj)
        {
            var list = new List<KeyValuePair<string, string[]>>();
            foreach (var prop in obj.Properties())
            {
                if (prop.Name == "OR" || prop.Name == "AND") continue;
                // A `when` value may be a bool or a `|`-separated list of acceptable strings ("low|tall").
                var raw = prop.Value.Type == JTokenType.Boolean
                    ? (prop.Value.Value<bool>() ? "true" : "false")
                    : prop.Value.ToString();
                list.Add(new KeyValuePair<string, string[]>(prop.Name, raw.Split('|')));
            }

            return list.ToArray();
        }

        // Blockstate x/y are degrees (multiples of 90) rotating the model about its centre — x then y. The
        // engine centres elements (-0.5..0.5) before the orient transform, so these are plain centre rotations.
        // The signs are negated to match the engine's axes (+X east, +Z south): e.g. the furnace's north-facing
        // model with y=90 then points east, as the pack intends.
        private static Matrix4X4<float> RotationFor(int x, int y) =>
            Matrix4X4.CreateRotationX(-(x * (MathF.PI / 180f))) *
            Matrix4X4.CreateRotationY(-(y * (MathF.PI / 180f)));

        // One multipart case: an OR of AND-groups (null = unconditional) plus the model to apply when it matches.
        private sealed class MultipartCase
        {
            public KeyValuePair<string, string[]>[][] AnyOf;
            public BlockStateVariant Variant;

            public bool Matches(IReadOnlyDictionary<string, string> state)
            {
                if (AnyOf == null) return true;
                foreach (var group in AnyOf)
                {
                    var ok = true;
                    foreach (var cond in group)
                        if (state == null || !state.TryGetValue(cond.Key, out var v) || Array.IndexOf(cond.Value, v) < 0)
                        {
                            ok = false;
                            break;
                        }

                    if (ok) return true;
                }

                return false;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using Newtonsoft.Json.Linq;

namespace MinecraftClone3API.Items
{
    /// <summary>
    /// Builds the crafting recipes from the resource pack's own <c>data/&lt;ns&gt;/recipe/*.json</c> (the
    /// Minecraft data format), resolving ingredient tags from <c>data/&lt;ns&gt;/tags/item/*.json</c> and mapping
    /// every Minecraft item id to the item we actually registered (by <see cref="Item.MinecraftId"/>). A recipe
    /// is registered only when its result and every ingredient cell resolve to at least one item we have, so a
    /// pack with thousands of recipes contributes exactly the ones craftable from the registered content. Runs
    /// once after all plugins load. Only shaped/shapeless crafting recipes are used; smelting/stonecutting/etc.
    /// are ignored.
    /// </summary>
    public static class RecipeLoader
    {
        public static void LoadFromResources()
        {
            var items = BuildItemMap();
            if (items.Count == 0) return;

            var tagCache = new Dictionary<string, HashSet<string>>();
            var loaded = 0;

            foreach (var key in new List<string>(ResourceManager.DataKeys))
            {
                if (!key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.IndexOf("/recipe/", StringComparison.OrdinalIgnoreCase) < 0 &&
                    key.IndexOf("/recipes/", StringComparison.OrdinalIgnoreCase) < 0) continue;

                try
                {
                    var recipe = ParseRecipe(key, items, tagCache);
                    if (recipe == null) continue;
                    GameRegistry.RegisterRecipe("minecraft", recipe);
                    loaded++;
                }
                catch (Exception e)
                {
                    Logger.Warn($"Could not load recipe \"{key}\"");
                    Logger.Exception(e);
                }
            }

            Logger.Info($"Loaded {loaded} crafting recipes from resources");
        }

        private static Dictionary<string, ushort> BuildItemMap()
        {
            var map = new Dictionary<string, ushort>();
            foreach (var item in GameRegistry.Items)
            {
                var id = NormalizeId(item.MinecraftId);
                if (id != null) map[id] = item.Id;
            }
            return map;
        }

        private static CraftingRecipe ParseRecipe(string dataKey, Dictionary<string, ushort> items,
            Dictionary<string, HashSet<string>> tagCache)
        {
            var json = JObject.Parse(Encoding.UTF8.GetString(ResourceManager.LoadData(dataKey)));
            var type = StripNamespace((string) json["type"]);
            if (type != "crafting_shaped" && type != "crafting_shapeless") return null;

            var result = ParseResult(json["result"], items);
            if (result.IsEmpty) return null;

            if (type == "crafting_shaped")
            {
                var pattern = json["pattern"] as JArray;
                var keyMap = json["key"] as JObject;
                if (pattern == null || keyMap == null || pattern.Count == 0) return null;

                var height = pattern.Count;
                var width = 0;
                foreach (var row in pattern)
                {
                    var len = ((string) row ?? "").Length;
                    if (len > width) width = len;
                }
                if (width == 0) return null;

                var cells = new ushort[width * height][];
                for (var y = 0; y < height; y++)
                {
                    var row = (string) pattern[y] ?? "";
                    for (var x = 0; x < width; x++)
                    {
                        var c = x < row.Length ? row[x] : ' ';
                        if (c == ' ') { cells[y * width + x] = null; continue; }

                        var set = MapIngredient(keyMap[c.ToString()], items, tagCache);
                        if (set.Length == 0) return null; // an ingredient we don't have → uncraftable
                        cells[y * width + x] = set;
                    }
                }

                return new ShapedRecipe(dataKey) { Width = width, Height = height, Pattern = cells, Result = result };
            }

            var ingredients = json["ingredients"] as JArray;
            if (ingredients == null || ingredients.Count == 0) return null;

            var list = new List<ushort[]>();
            foreach (var token in ingredients)
            {
                var set = MapIngredient(token, items, tagCache);
                if (set.Length == 0) return null;
                list.Add(set);
            }

            return new ShapelessRecipe(dataKey) { Ingredients = list.ToArray(), Result = result };
        }

        private static ItemStack ParseResult(JToken token, Dictionary<string, ushort> items)
        {
            if (token == null) return ItemStack.Empty;

            string id;
            var count = 1;
            if (token.Type == JTokenType.Object)
            {
                id = (string) token["id"] ?? (string) token["item"];
                if (token["count"] != null) count = token["count"].Value<int>();
            }
            else
            {
                id = (string) token;
            }

            id = NormalizeId(id);
            return id != null && items.TryGetValue(id, out var itemId)
                ? new ItemStack(itemId, count)
                : ItemStack.Empty;
        }

        /// <summary>Resolves an ingredient token (a single id, a <c>#tag</c>, an object with <c>item</c>/<c>tag</c>,
        /// or an array of any of these) to the distinct ids of the items we actually have.</summary>
        private static ushort[] MapIngredient(JToken token, Dictionary<string, ushort> items,
            Dictionary<string, HashSet<string>> tagCache)
        {
            var mcIds = new HashSet<string>();
            CollectIds(token, mcIds, tagCache, new HashSet<string>());

            var result = new List<ushort>();
            foreach (var mcId in mcIds)
                if (items.TryGetValue(mcId, out var id) && !result.Contains(id))
                    result.Add(id);
            return result.ToArray();
        }

        private static void CollectIds(JToken token, HashSet<string> into,
            Dictionary<string, HashSet<string>> tagCache, HashSet<string> visitedTags)
        {
            if (token == null) return;

            switch (token.Type)
            {
                case JTokenType.Array:
                    foreach (var t in token) CollectIds(t, into, tagCache, visitedTags);
                    break;
                case JTokenType.Object:
                    if (token["tag"] != null) into.UnionWith(ResolveTag((string) token["tag"], tagCache, visitedTags));
                    else if (token["item"] != null) AddId((string) token["item"], into);
                    break;
                case JTokenType.String:
                    var s = (string) token;
                    if (!string.IsNullOrEmpty(s) && s[0] == '#') into.UnionWith(ResolveTag(s.Substring(1), tagCache, visitedTags));
                    else AddId(s, into);
                    break;
            }
        }

        private static HashSet<string> ResolveTag(string tag, Dictionary<string, HashSet<string>> tagCache,
            HashSet<string> visitedTags)
        {
            var id = NormalizeId(tag);
            if (id == null) return new HashSet<string>();
            if (tagCache.TryGetValue(id, out var cached)) return cached;
            if (!visitedTags.Add(id)) return new HashSet<string>(); // cycle guard

            var result = new HashSet<string>();
            var colon = id.IndexOf(':');
            var dataKey = id.Substring(0, colon) + "/tags/item/" + id.Substring(colon + 1) + ".json";
            if (ResourceManager.ExistsData(dataKey))
            {
                try
                {
                    var json = JObject.Parse(Encoding.UTF8.GetString(ResourceManager.LoadData(dataKey)));
                    if (json["values"] is JArray values)
                        foreach (var v in values)
                        {
                            // A value is a member id, a "#tag" reference, or an object {"id":..,"required":..}.
                            var member = v.Type == JTokenType.Object ? (string) v["id"] : (string) v;
                            if (string.IsNullOrEmpty(member)) continue;
                            if (member[0] == '#') result.UnionWith(ResolveTag(member.Substring(1), tagCache, visitedTags));
                            else AddId(member, result);
                        }
                }
                catch (Exception e)
                {
                    Logger.Warn($"Could not parse item tag \"{dataKey}\"");
                    Logger.Exception(e);
                }
            }

            tagCache[id] = result;
            return result;
        }

        private static void AddId(string id, HashSet<string> into)
        {
            var n = NormalizeId(id);
            if (n != null) into.Add(n);
        }

        private static string NormalizeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return id.IndexOf(':') >= 0 ? id : "minecraft:" + id;
        }

        private static string StripNamespace(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            var colon = id.IndexOf(':');
            return colon < 0 ? id : id.Substring(colon + 1);
        }
    }
}

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Helpers for the namespaced <c>namespace:name</c> identifiers Minecraft uses for blocks, items, tags and
    /// recipes (e.g. <c>"minecraft:oak_planks"</c>). These ids are what tie our registered content to the
    /// resource pack's own data — its <c>data/</c> recipes/tags and its <c>lang</c> translations — so a pack's
    /// content drives the game rather than hardcoded tables.
    /// </summary>
    public static class Identifier
    {
        /// <summary>Derives the content id from a resource path: the namespace is the part before a <c>:</c>
        /// (or the first path segment, else <c>minecraft</c>) and the name is the final path segment without its
        /// extension — e.g. <c>"minecraft:block/stone"</c> → <c>"minecraft:stone"</c> and
        /// <c>"minecraft/textures/item/stick.png"</c> → <c>"minecraft:stick"</c>.</summary>
        public static string FromResourcePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var ns = "minecraft";
            var rest = path;

            var colon = path.IndexOf(':');
            if (colon >= 0)
            {
                ns = path.Substring(0, colon);
                rest = path.Substring(colon + 1);
            }
            else
            {
                var slash = path.IndexOf('/');
                if (slash >= 0)
                {
                    ns = path.Substring(0, slash);
                    rest = path.Substring(slash + 1);
                }
            }

            var lastSlash = rest.LastIndexOf('/');
            var name = lastSlash >= 0 ? rest.Substring(lastSlash + 1) : rest;
            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(0, dot);

            return ns + ":" + name;
        }

        /// <summary>The Minecraft translation key for a content id under a category, e.g.
        /// (<c>"block"</c>, <c>"minecraft:stone"</c>) → <c>"block.minecraft.stone"</c>, matching the keys in a
        /// resource pack's <c>lang/*.json</c>.</summary>
        public static string TranslationKey(string category, string id)
        {
            if (string.IsNullOrEmpty(id)) return category;
            var colon = id.IndexOf(':');
            var ns = colon < 0 ? "minecraft" : id.Substring(0, colon);
            var name = colon < 0 ? id : id.Substring(colon + 1);
            return category + "." + ns + "." + name;
        }
    }
}

using System;
using System.Collections.Generic;
using MinecraftClone3API.IO;

namespace MinecraftClone3API.Util
{
    public static class I18N
    {
        private const string OrdinalLang = "en-US";

        private static readonly Dictionary<string, string> Entries = new Dictionary<string, string>();

        private static string _currentLang = "en-US";

        public static void SetCurrentLanguage(string lang) => _currentLang = lang;

        public static void Load(Action<float> progress)
        {
            Entries.Clear();

            var indices = new Dictionary<string, int>();
            var part = 1f / ResourceManager.LangEntries.Count;
            var total = 0f;
            ResourceManager.LangEntries.ForEach(entry =>
            {
                progress(total);
                total += part;
                
                var splits = entry.Line.Split('=');
                if (splits.Length != 2) return;
                var key = splits[0];
                var value = splits[1];

                var globalKey = MakeKey(entry.Lang, key);

                if (indices.TryGetValue(globalKey, out var index))
                    if (entry.Index < index) return;

                indices[globalKey] = entry.Index;
                Entries[globalKey] = value;
            });
        }

        public static string GetLang(string lang, string key) => Entries.TryGetValue(MakeKey(lang, key), out var value)
            ? value
            : key;
        public static string Get(string key) => GetLang(_currentLang, key);
        public static string GetOrdinal(string key) => GetLang(OrdinalLang, key);

        /// <summary>Builds the unlocalized lang key for a registry entry: a <c>"prefix:Name"</c> registry key
        /// becomes <c>"prefix.category.Name"</c> (prefix lower-cased), e.g. <c>"Vanilla:Stone"</c> with
        /// category <c>"blocks"</c> → <c>"vanilla.blocks.Stone"</c>, matching the plugin lang files.</summary>
        public static string UnlocalizedName(string registryKey, string category)
        {
            if (string.IsNullOrEmpty(registryKey)) return category;
            var colon = registryKey.IndexOf(':');
            return colon < 0
                ? registryKey
                : registryKey.Substring(0, colon).ToLowerInvariant() + "." + category + "." + registryKey.Substring(colon + 1);
        }

        private static string MakeKey(string lang, string key) => $"{lang}:{key}";
    }
}

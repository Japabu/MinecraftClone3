using System.Collections.Generic;

namespace MinecraftClone3API.Util
{
    public class Registry<T> where T : RegistryEntry
    {
        private readonly Dictionary<string, T> _keysToEntries = new Dictionary<string, T>();
        private readonly Dictionary<T, string> _entriesToKeys = new Dictionary<T, string>();

        public T this[string key] => _keysToEntries[key];
        public string this[T entry] => _entriesToKeys[entry];

        public IEnumerable<T> Values => _keysToEntries.Values;

        public bool TryGet(string key, out T entry) => _keysToEntries.TryGetValue(key, out entry);

        public virtual void Register(string prefix, T entry)
        {
            entry.RegistryKey = $"{prefix}:{entry.Name}";
            _keysToEntries.Add(entry.RegistryKey, entry);
            _entriesToKeys.Add(entry, entry.RegistryKey);
        }
    }
}
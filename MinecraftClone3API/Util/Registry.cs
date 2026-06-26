using System.Collections.Generic;
using System.Collections.Concurrent;

namespace MinecraftClone3API.Util
{
    public class Registry<T> where T : RegistryEntry
    {
        // Concurrent so a runtime placeholder registration (a missing block/item name resolved on the chunk
        // load thread, see BlockRegistry/ItemRegistry.GetOrRegisterUnknown) is safe against the many threads
        // that read the maps. Startup registration is single-threaded; reads never allocate.
        private readonly ConcurrentDictionary<string, T> _keysToEntries = new ConcurrentDictionary<string, T>();
        private readonly ConcurrentDictionary<T, string> _entriesToKeys = new ConcurrentDictionary<T, string>();

        public T this[string key] => _keysToEntries[key];
        public string this[T entry] => _entriesToKeys[entry];

        public IEnumerable<T> Values => _keysToEntries.Values;

        public bool TryGet(string key, out T entry) => _keysToEntries.TryGetValue(key, out entry);

        public virtual void Register(string prefix, T entry) => RegisterWithKey($"{prefix}:{entry.Name}", entry);

        /// <summary>Registers <paramref name="entry"/> under an explicit, fully-qualified key rather than the
        /// <c>prefix:Name</c> form. Used for placeholder entries whose key is a saved name we must preserve
        /// verbatim so it round-trips on the next save.</summary>
        protected void RegisterWithKey(string registryKey, T entry)
        {
            entry.RegistryKey = registryKey;
            if (!_keysToEntries.TryAdd(registryKey, entry))
                throw new System.ArgumentException($"Duplicate registry key {registryKey}");
            _entriesToKeys[entry] = registryKey;
        }
    }
}

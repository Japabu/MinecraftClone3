using System.Collections.Concurrent;
using MinecraftClone3API.Items;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Registry of <see cref="Item"/>s, parallel to <see cref="BlockRegistry"/> but with its own id space.
    /// Id 0 is reserved for the empty stack, so real items start at 1. A block's auto-generated
    /// <see cref="ItemBlock"/> and standalone items share this registry. The numeric id is a session-local
    /// detail only: inventories persist a stack's stable <see cref="RegistryEntry.RegistryKey"/> (name), not
    /// its id, so adding/removing/reordering items never corrupts a saved inventory. A saved name that no
    /// longer resolves is preserved as an inert <see cref="ItemUnknown"/> via <see cref="GetOrRegisterUnknown"/>.
    /// </summary>
    public class ItemRegistry : Registry<Item>
    {
        private readonly ConcurrentDictionary<ushort, Item> _idsToItems = new ConcurrentDictionary<ushort, Item>();
        private readonly ConcurrentDictionary<string, ushort> _keysToIds = new ConcurrentDictionary<string, ushort>();
        private readonly object _registerLock = new object();

        public Item this[ushort id] => _idsToItems[id];

        public bool TryGet(ushort id, out Item item) => _idsToItems.TryGetValue(id, out item);

        public sealed override void Register(string prefix, Item item)
        {
            base.Register(prefix, item);
            lock (_registerLock) AssignId(item);
        }

        /// <summary>Resolves a saved item name to a runtime id, minting an inert <see cref="ItemUnknown"/>
        /// (keeping the name verbatim) when the name no longer resolves, so the stack round-trips losslessly.</summary>
        public ushort GetOrRegisterUnknown(string registryKey)
        {
            if (_keysToIds.TryGetValue(registryKey, out var id)) return id;
            lock (_registerLock)
            {
                if (_keysToIds.TryGetValue(registryKey, out id)) return id;
                var item = new ItemUnknown(registryKey);
                RegisterWithKey(registryKey, item);
                AssignId(item);
                return item.Id;
            }
        }

        private void AssignId(Item item)
        {
            item.Id = GetItemId(item);
            _idsToItems[item.Id] = item;
            _keysToIds[item.RegistryKey] = item.Id;
        }

        private ushort GetItemId(Item item)
        {
            if (_keysToIds.TryGetValue(item.RegistryKey, out var id)) return id;

            id = 1;
            while (_idsToItems.ContainsKey(id)) id++;
            return id;
        }
    }
}

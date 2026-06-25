using System.Collections.Generic;
using System.IO;
using MinecraftClone3API.Items;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Registry of <see cref="Item"/>s, parallel to <see cref="BlockRegistry"/> but with its own id space
    /// (ids only travel in inventory packets, never in chunk storage). Id 0 is reserved for the empty stack,
    /// so real items start at 1. A block's auto-generated <see cref="ItemBlock"/> and standalone items share
    /// this registry; ids are assigned in registration (enumeration) order, deterministic for a fixed plugin set.
    /// </summary>
    public class ItemRegistry : Registry<Item>
    {
        private readonly Dictionary<ushort, Item> _idsToItems = new Dictionary<ushort, Item>();
        private readonly Dictionary<string, ushort> _keysToIds = new Dictionary<string, ushort>();

        public Item this[ushort id] => _idsToItems[id];

        public bool TryGet(ushort id, out Item item) => _idsToItems.TryGetValue(id, out item);

        public sealed override void Register(string prefix, Item item)
        {
            base.Register(prefix, item);

            item.Id = GetItemId(item);
            _idsToItems.Add(item.Id, item);
            _keysToIds[item.RegistryKey] = item.Id;
        }

        internal void Write(BinaryWriter writer)
        {
            writer.Write(_keysToIds.Count);
            foreach (var entry in _keysToIds)
            {
                writer.Write(entry.Key);
                writer.Write(entry.Value);
            }
        }

        internal void Read(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
                _keysToIds[reader.ReadString()] = reader.ReadUInt16();
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

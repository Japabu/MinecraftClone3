using System.Collections.Generic;
using MinecraftClone3API.Entities;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Registry of <see cref="EntityType"/>s. Assigns each a sequential numeric <see cref="EntityType.Id"/>
    /// in registration order; since the client and the server load the same plugins in the same order, the
    /// ids agree on both sides and can be sent on the wire (the same contract as block ids). Entities are
    /// transient (never persisted to disk), so the ids need only be stable within a session, not across runs.
    /// </summary>
    public class EntityRegistry : Registry<EntityType>
    {
        private readonly Dictionary<ushort, EntityType> _idsToTypes = new Dictionary<ushort, EntityType>();
        private ushort _nextId;

        public EntityType this[ushort id] => _idsToTypes[id];

        public bool TryGet(ushort id, out EntityType type) => _idsToTypes.TryGetValue(id, out type);

        public sealed override void Register(string prefix, EntityType entry)
        {
            base.Register(prefix, entry);
            entry.Id = _nextId++;
            _idsToTypes.Add(entry.Id, entry);
        }
    }
}

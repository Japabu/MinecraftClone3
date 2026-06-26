using System;

namespace MinecraftClone3API.Util
{
    internal class EntityDataRegistryEntry : RegistryEntry, IEquatable<EntityDataRegistryEntry>
    {
        public readonly Type Type;

        public EntityDataRegistryEntry(Type type) : base(type.Name)
        {
            Type = type;
        }

        public override int GetHashCode() => Type.GetHashCode();
        public override bool Equals(object obj)
        {
            var v = obj as EntityDataRegistryEntry;
            return v != null && Equals(this, v);
        }

        public bool Equals(EntityDataRegistryEntry other) => other != null && Type == other.Type;
    }
}

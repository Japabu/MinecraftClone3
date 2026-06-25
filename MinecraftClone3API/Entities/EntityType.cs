using System;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Entities
{
    public enum EntityKind
    {
        /// <summary>A walking creature (animal or mob) with wander/chase AI and a box model.</summary>
        Creature,

        /// <summary>A dropped item stack: a small spinning block icon affected by gravity, picked up by players.</summary>
        Item
    }

    /// <summary>
    /// A registered species of entity. Shared client/server: the server reads <see cref="Width"/>/<see
    /// cref="Height"/> for collision and the AI fields for behaviour; the client additionally reads
    /// <see cref="TexturePath"/> and <see cref="ModelFactory"/> to build its render model. The model
    /// description is GL-free data, so the headless server holds it harmlessly. Numeric <see cref="Id"/>s
    /// are assigned by registration order (client and server load the same plugins, so the ids agree —
    /// the same block-id-agreement contract); player entities use the reserved <see cref="PlayerTypeId"/>.
    /// </summary>
    public class EntityType : RegistryEntry
    {
        /// <summary>Reserved wire id for a remote player (rendered with the built-in humanoid + skin path).</summary>
        public const ushort PlayerTypeId = ushort.MaxValue;

        public ushort Id { get; internal set; }

        public readonly float Width;
        public readonly float Height;
        public readonly EntityKind Kind;

        /// <summary>Hostile creatures path toward the nearest player within sight; non-hostile ones only wander.</summary>
        public readonly bool Hostile;

        /// <summary>Wander/chase ground speed in blocks per tick.</summary>
        public readonly float MoveSpeed;

        public readonly float MaxHealth;

        /// <summary>Entity texture resource location (e.g. <c>minecraft:entity/pig/pig</c>); null for items.</summary>
        public readonly string TexturePath;

        /// <summary>Builds the client render model (box parts). Invoked once, lazily, on the client; never on
        /// the headless server. Null for items (they render the dropped block's icon mesh instead).</summary>
        public readonly Func<EntityModel> ModelFactory;

        public EntityType(string name, EntityKind kind, float width, float height, float maxHealth,
            float moveSpeed, bool hostile, string texturePath, Func<EntityModel> modelFactory) : base(name)
        {
            Kind = kind;
            Width = width;
            Height = height;
            MaxHealth = maxHealth;
            MoveSpeed = moveSpeed;
            Hostile = hostile;
            TexturePath = texturePath;
            ModelFactory = modelFactory;
        }

        /// <summary>Creates a server-side instance of this type (used by <c>WorldServer.SpawnEntity</c>).</summary>
        public Entity CreateEntity()
        {
            var entity = Kind == EntityKind.Item ? (Entity) new EntityItem() : new EntityCreature();
            entity.Type = this;
            return entity;
        }
    }
}

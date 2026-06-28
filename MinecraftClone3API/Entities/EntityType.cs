using System;
using System.Collections.Generic;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Entities
{
    public enum EntityKind
    {
        /// <summary>A walking creature (animal or mob) with wander/chase AI and a box model.</summary>
        Creature,

        /// <summary>A dropped item stack: a small spinning block icon affected by gravity, picked up by players.</summary>
        Item,

        /// <summary>A block falling under gravity (sand, gravel): rendered as a full-size block, turns back into
        /// a placed block when it lands.</summary>
        FallingBlock,

        /// <summary>A thrown projectile (the ender pearl): flies under light gravity until it hits a block, then
        /// acts on its thrower. Rendered as a small flat sprite of its item texture.</summary>
        Projectile
    }

    /// <summary>
    /// A registered species of entity. Shared client/server: the server reads <see cref="Width"/>/<see
    /// cref="Height"/> for collision and the AI fields for behaviour; the client additionally reads
    /// <see cref="TexturePath"/> and <see cref="ModelPath"/> to build its render model. The model is loaded
    /// from a Bedrock geometry data file, so the headless server never touches it. Numeric <see cref="Id"/>s
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

        /// <summary>A neutral mob (the enderman): wanders peacefully until a player looks directly at it, then
        /// chases that player until they escape. Independent of <see cref="Hostile"/> (which is chase-on-sight).</summary>
        public readonly bool NeutralUntilProvoked;

        /// <summary>Wander/chase ground speed in blocks per tick.</summary>
        public readonly float MoveSpeed;

        public readonly float MaxHealth;

        /// <summary>Entity texture resource location (e.g. <c>minecraft:entity/pig/pig</c>); null for items.</summary>
        public readonly string TexturePath;

        /// <summary>Resource key of the Bedrock geometry file for the client render model (e.g.
        /// <c>Vanilla/Models/Entity/cow.geo.json</c>). Read once, lazily, on the client; never on the headless
        /// server. Null for items (they render the dropped block's icon mesh instead).</summary>
        public readonly string ModelPath;

        /// <summary>Optional second render layer drawn over the base model with its own texture — the sheep's
        /// wool. Suppressed per-entity by its <see cref="EntityData.OverlayVisible"/>. Null for types without one.</summary>
        public readonly string OverlayModelPath;
        public readonly string OverlayTexturePath;

        /// <summary>Builds the initial <see cref="EntityData"/> a freshly-spawned instance carries (e.g. a sheep's
        /// wool state), or null for types that need none.</summary>
        public readonly Func<EntityData> DataFactory;

        /// <summary>Melee damage (half-hearts) a hostile creature deals on contact; 0 for passive animals.</summary>
        public readonly float AttackDamage;

        /// <summary>What this creature drops when killed, or null for none.</summary>
        public readonly LootTable Loot;

        /// <summary>Dimension RegistryKeys (e.g. <c>Vanilla:Overworld</c>) this type may ambient-spawn in, matched
        /// against <c>WorldServer.DimensionKey</c>. Null = never ambient-spawned (items, projectiles, player-only
        /// types). Kept plugin-supplied so Core never hardcodes a dimension.</summary>
        public readonly HashSet<string> SpawnableDimensions;

        public EntityType(string name, EntityKind kind, float width, float height, float maxHealth,
            float moveSpeed, bool hostile, string texturePath, string modelPath,
            string overlayModelPath = null, string overlayTexturePath = null, Func<EntityData> dataFactory = null,
            float attackDamage = 0f, LootTable loot = null, bool neutralUntilProvoked = false,
            string[] spawnableDimensions = null)
            : base(name)
        {
            Kind = kind;
            Width = width;
            Height = height;
            MaxHealth = maxHealth;
            MoveSpeed = moveSpeed;
            Hostile = hostile;
            NeutralUntilProvoked = neutralUntilProvoked;
            TexturePath = texturePath;
            ModelPath = modelPath;
            OverlayModelPath = overlayModelPath;
            OverlayTexturePath = overlayTexturePath;
            DataFactory = dataFactory;
            AttackDamage = attackDamage;
            Loot = loot;
            SpawnableDimensions = spawnableDimensions == null ? null : new HashSet<string>(spawnableDimensions);
        }

        /// <summary>Whether ambient spawning may place this type in the given dimension. Null/empty
        /// <see cref="SpawnableDimensions"/> means never (so an untagged type can't leak into every dimension).</summary>
        public bool CanSpawnInDimension(string dimensionKey)
            => SpawnableDimensions != null && SpawnableDimensions.Contains(dimensionKey);

        /// <summary>Creates a server-side instance of this type (used by <c>WorldServer.SpawnEntity</c>).</summary>
        public Entity CreateEntity()
        {
            Entity entity;
            switch (Kind)
            {
                case EntityKind.Item: entity = new EntityItem(); break;
                case EntityKind.FallingBlock: entity = new EntityFallingBlock(); break;
                case EntityKind.Projectile: entity = new EntityProjectile(); break;
                default: entity = new EntityCreature(); break;
            }
            entity.Type = this;
            entity.Data = DataFactory?.Invoke();
            return entity;
        }
    }
}

using System.Collections.Generic;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Blocks
{
    public enum TransparencyType
    {
        None,
        Cutoff,
        Transparent
    }
    public enum ConnectionType
    {
        Undefined,
        Connected,
        Disconnected
    }

    /// <summary>
    /// A hint for how the deferred composition shader should shade a block's surface, baked into the G-buffer
    /// normal.w by the mesher (see <c>ChunkMesher.WaterNormalW</c>). A new value needs both a mesher w-mapping
    /// and a matching detection band in <c>Composition.fs</c>; values without their own mapping fall back to
    /// <see cref="Solid"/> (lit normally).
    /// </summary>
    public enum RenderMaterial
    {
        Solid,
        Water
    }

    public class Block : RegistryEntry
    {
        public static readonly AxisAlignedBoundingBox DefaultAlignedBoundingBox =
            new AxisAlignedBoundingBox(new Vector3(-0.5f), new Vector3(0.5f));
        
        public BlockModel Model = CommonResources.MissingModel;

        public Block(string name) : base(name)
        {
        }

        public ushort Id { get; internal set; }

        /// <summary>The block's Minecraft content id (e.g. <c>"minecraft:stone"</c>), used to resolve its name
        /// from the resource pack's translations and to match the pack's crafting recipes/tags. Set by
        /// <c>BlockBasic</c> from the model path; custom blocks whose model path doesn't match (water, …) set
        /// it explicitly. Null for blocks with no Minecraft equivalent.</summary>
        public string MinecraftId;

        public virtual bool IsVisible(WorldBase world, Vector3i blockPos) => true;
        public virtual bool IsFullBlock(WorldBase world, Vector3i blockPos) => true;
        public virtual TransparencyType IsTransparent(WorldBase world, Vector3i blockPos) => TransparencyType.None;

        public virtual RenderMaterial GetRenderMaterial(WorldBase world, Vector3i blockPos) => RenderMaterial.Solid;

        public virtual ConnectionType ConnectsToBlock(WorldBase world, Vector3i blockPos, Vector3i otherBlockPos,
            Block otherBlock) => ConnectionType.Undefined;

        public virtual bool CanPassThrough(WorldBase world, Vector3i blockPos) => false;
        public virtual bool CanTarget(WorldBase world, Vector3i vector3I) => true;
        public virtual bool IsLiquid => false;

        public virtual AxisAlignedBoundingBox GetBoundingBox(WorldBase world, Vector3i blockPos)
            => DefaultAlignedBoundingBox;

        /// <summary>The solid collision boxes (block-local, centred -0.5..0.5) the player sweeps against.
        /// Default is the single <see cref="GetBoundingBox"/> cube; non-cube blocks (stairs) override to
        /// return several boxes. Pass-through blocks contribute none. Kept separate from
        /// <see cref="GetBoundingBox"/> so targeting/raytrace can stay a single simple cube.</summary>
        public virtual void GetCollisionBoxes(WorldBase world, Vector3i blockPos, List<AxisAlignedBoundingBox> boxes)
        {
            if (CanPassThrough(world, blockPos)) return;
            var bb = GetBoundingBox(world, blockPos);
            if (bb != null) boxes.Add(bb);
        }

        /// <summary>Per-block model orientation applied at mesh time (composed after the element transform,
        /// so it rotates about the block centre). Default identity. The engine parses no blockstate files,
        /// so orientation that vanilla keeps in the blockstate (e.g. a stair's facing) lives here, driven by
        /// the block's stored metadata.</summary>
        public virtual Matrix4 GetModelTransform(WorldBase world, Vector3i blockPos) => Matrix4.Identity;

        /// <summary>The block's parsed blockstate definition (from the pack's <c>blockstates/&lt;name&gt;.json</c>),
        /// or null if it has none. When set, the mesher selects the model + rotation for the block's current
        /// <see cref="GetBlockState"/> from this definition instead of using the single <see cref="Model"/>.</summary>
        public BlockStateDefinition StateDefinition;

        /// <summary>This block's current blockstate property values (e.g. <c>facing=east, lit=true</c>) derived
        /// from its stored block data, used to pick a variant from <see cref="StateDefinition"/>. Null/empty for
        /// stateless blocks (matches the unconditional <c>""</c> variant).</summary>
        public virtual IReadOnlyDictionary<string, string> GetBlockState(WorldBase world, Vector3i blockPos) => null;

        /// <summary>The model and orientation the mesher should emit for this block at this position. When the
        /// block has a <see cref="StateDefinition"/>, both come from the variant matching <see cref="GetBlockState"/>
        /// (so a furnace's facing/lit appearance is driven straight from the pack's blockstate file); otherwise
        /// the single <see cref="Model"/> with <see cref="GetModelTransform"/>.</summary>
        public virtual (BlockModel Model, Matrix4 Orient) GetRenderModel(WorldBase world, Vector3i blockPos)
        {
            if (StateDefinition != null)
            {
                var variant = StateDefinition.Resolve(GetBlockState(world, blockPos));
                if (variant != null) return (variant.Model, variant.Rotation);
            }
            return (Model, GetModelTransform(world, blockPos));
        }

        /// <summary>True for blocks whose <see cref="OnServerTick"/> must run every server tick (e.g. a smelting
        /// furnace). The server keeps a registry of such block positions so it ticks only them.</summary>
        public virtual bool NeedsServerTick => false;

        /// <summary>Server-authoritative per-tick update for a <see cref="NeedsServerTick"/> block (never called
        /// on the client). Reads/writes the block's stored <see cref="BlockData"/>.</summary>
        public virtual void OnServerTick(WorldServer world, Vector3i blockPos) { }

        /// <summary>Server-side: a block in one of the six face-adjacent positions changed (placed, broken, or
        /// fell). Default no-op. Overriders must stay light — only schedule a tick
        /// (<see cref="WorldServer.ScheduleBlockTick"/>) or touch their own block data; do not call back into
        /// <c>SetBlock</c>, which would recurse through this notification. Falling blocks use it to start falling
        /// when the block beneath them is removed.</summary>
        public virtual void OnNeighborChanged(WorldServer world, Vector3i blockPos, Vector3i changedPos) { }

        public virtual Color4 GetTintColor(WorldBase world, Vector3i blockPos, int tintId) => Color4.White;
        public virtual LightLevel GetLightLevel(WorldBase world, Vector3i blockPos) => LightLevel.Zero;

        public virtual void OnPlaced(WorldBase world, Vector3i blockPos, EntityPlayer player, int metadata)
        {
        }

        /// <summary>Client-side: derive the metadata to carry in the place request (e.g. a stair's facing from
        /// the placing player's look + clicked face). Runs on the client; takes only Core types so the headless
        /// server never depends on input/windowing.</summary>
        public virtual int GetPlacementMetadata(EntityPlayer player, BlockRaytraceResult ray) => 0;

        /// <summary>Client-side: the player right-clicked this block while looking at it. Return true if the
        /// block handled the interaction (e.g. opened a GUI), which suppresses placing the held item. Runs
        /// only on the client (the headless server never calls it), so it may touch window/GUI state via the
        /// client globals (<c>ClientResources.Window</c>, <c>StateEngine</c>) — not passed in, so Core's
        /// signature stays free of windowing types.</summary>
        public virtual bool OnActivated(WorldBase world, Vector3i blockPos, EntityPlayer player) => false;

        public virtual int OnLightPassThrough(WorldBase world, Vector3i blockPos, int lightLevel, int color)
            => lightLevel - 1;

        public virtual string GetUnlocalizedName(WorldBase world, Vector3i blockPos) =>
            MinecraftId != null
                ? Identifier.TranslationKey("block", MinecraftId)
                : I18N.UnlocalizedName(RegistryKey, "blocks");

        public virtual string GetName(WorldBase world, Vector3i blockPos) => I18N.Get(GetUnlocalizedName(world, blockPos));

        public bool IsOpaqueFullBlock(WorldBase world, Vector3i blockPos) =>
            IsVisible(world, blockPos) && IsFullBlock(world, blockPos) &&
            IsTransparent(world, blockPos) == TransparencyType.None;
    }
}

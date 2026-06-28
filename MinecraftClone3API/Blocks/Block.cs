using System.Collections.Generic;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

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
            new AxisAlignedBoundingBox(Vector3D<float>.Zero, new Vector3D<float>(1f));
        
        public BlockModel Model = CommonResources.MissingModel;

        /// <summary>Resource-pack path of this block's model, declared in the constructor. It is parsed into
        /// <see cref="Model"/> by the client's load pass (<see cref="LoadModel"/>) — the headless server never
        /// resolves it, so it reads no models or textures. Null for blocks with no model (e.g. air).</summary>
        public string ModelPath { get; protected set; }

        public Block(string name) : base(name)
        {
        }

        public ushort Id { get; internal set; }

        /// <summary>The block's Minecraft content id (e.g. <c>"minecraft:stone"</c>), used to resolve its name
        /// from the resource pack's translations and to match the pack's crafting recipes/tags. Set by
        /// <c>BlockBasic</c> from the model path; custom blocks whose model path doesn't match (water, …) set
        /// it explicitly. Null for blocks with no Minecraft equivalent.</summary>
        public string MinecraftId;

        /// <summary>The creative-menu tab this block (and its auto-generated <see cref="ItemBlock"/>) appears
        /// under. Defaults to <see cref="DefaultCreativeTab"/>; set explicitly at registration to override a
        /// single inline block.</summary>
        public CreativeTab CreativeTab { get => _creativeTab ?? DefaultCreativeTab; set => _creativeTab = value; }
        private CreativeTab? _creativeTab;

        /// <summary>The tab a block falls in when none is set; subclasses override to categorise a whole family
        /// (ores/logs/terrain → Natural, glass → Coloured, containers → Functional). Plain building materials
        /// keep the default.</summary>
        protected virtual CreativeTab DefaultCreativeTab => CreativeTab.BuildingBlocks;

        public virtual bool IsVisible(WorldBase world, Vector3D<int> blockPos) => true;
        public virtual bool IsFullBlock(WorldBase world, Vector3D<int> blockPos) => true;
        public virtual TransparencyType IsTransparent(WorldBase world, Vector3D<int> blockPos) => TransparencyType.None;

        public virtual RenderMaterial GetRenderMaterial(WorldBase world, Vector3D<int> blockPos) => RenderMaterial.Solid;

        public virtual ConnectionType ConnectsToBlock(WorldBase world, Vector3D<int> blockPos, Vector3D<int> otherBlockPos,
            Block otherBlock) => ConnectionType.Undefined;

        public virtual bool CanPassThrough(WorldBase world, Vector3D<int> blockPos) => false;
        public virtual bool CanTarget(WorldBase world, Vector3D<int> vector3I) => true;
        public virtual bool IsLiquid => false;

        /// <summary>Minecraft block hardness, driving how long the block takes to mine in survival
        /// (<c>break seconds ≈ hardness · 1.5</c> by hand). A negative value is unbreakable (bedrock). Creative
        /// breaks instantly regardless.</summary>
        public virtual float Hardness => 1.5f;

        /// <summary>The tool category that mines this block fastest (Minecraft's <c>mineable/*</c> tags), or
        /// <see cref="ToolType.None"/> if no tool helps. A matching held tool applies its
        /// <see cref="Item.MiningSpeed"/> multiplier.</summary>
        public virtual ToolType PreferredTool => ToolType.None;

        /// <summary>Whether mining at full speed requires the correct tool of sufficient tier (Minecraft's
        /// <c>requires_tool</c>: stone, ores, obsidian). When true and the held tool is wrong or too low a tier,
        /// mining is throttled (the vanilla ÷100 vs ÷30 penalty).</summary>
        public virtual bool RequiresCorrectTool => false;

        /// <summary>The minimum matching-tool tier needed to satisfy <see cref="RequiresCorrectTool"/> (Minecraft
        /// harvest level: stone ore 1, gold/diamond ore 2, obsidian 3). Ignored when no tool is required.</summary>
        public virtual int RequiredToolTier => 0;

        public virtual AxisAlignedBoundingBox GetBoundingBox(WorldBase world, Vector3D<int> blockPos)
            => DefaultAlignedBoundingBox;

        /// <summary>The solid collision boxes (block-local 0..1, so block P fills [P, P+1]) the player sweeps against.
        /// Default is the single <see cref="GetBoundingBox"/> cube; non-cube blocks (stairs) override to
        /// return several boxes. Pass-through blocks contribute none. Kept separate from
        /// <see cref="GetBoundingBox"/> so targeting/raytrace can stay a single simple cube.</summary>
        public virtual void GetCollisionBoxes(WorldBase world, Vector3D<int> blockPos, List<AxisAlignedBoundingBox> boxes)
        {
            if (CanPassThrough(world, blockPos)) return;
            var bb = GetBoundingBox(world, blockPos);
            if (bb != null) boxes.Add(bb);
        }

        /// <summary>Per-block model orientation applied at mesh time (composed after the element transform,
        /// so it rotates about the block centre). Default identity. The engine parses no blockstate files,
        /// so orientation that vanilla keeps in the blockstate (e.g. a stair's facing) lives here, driven by
        /// the block's stored metadata.</summary>
        public virtual Matrix4X4<float> GetModelTransform(WorldBase world, Vector3D<int> blockPos) => Matrix4X4<float>.Identity;

        /// <summary>The block's parsed blockstate definition (from the pack's <c>blockstates/&lt;name&gt;.json</c>),
        /// or null if it has none. When set, the mesher selects the model + rotation for the block's current
        /// <see cref="GetBlockState"/> from this definition instead of using the single <see cref="Model"/>.</summary>
        public BlockStateDefinition StateDefinition;

        /// <summary>Content id whose <c>blockstates/&lt;name&gt;.json</c> drives this block's per-state model
        /// selection (e.g. a furnace's facing/lit), declared in the constructor; null for blocks without one.
        /// Resolved into <see cref="StateDefinition"/> by <see cref="LoadModel"/> on the client.</summary>
        public string BlockStateId { get; protected set; }

        /// <summary>This block's current blockstate property values (e.g. <c>facing=east, lit=true</c>) derived
        /// from its stored block data, used to pick a variant from <see cref="StateDefinition"/>. Null/empty for
        /// stateless blocks (matches the unconditional <c>""</c> variant).</summary>
        public virtual IReadOnlyDictionary<string, string> GetBlockState(WorldBase world, Vector3D<int> blockPos) => null;

        /// <summary>The model and orientation the mesher should emit for this block at this position. When the
        /// block has a <see cref="StateDefinition"/>, both come from the variant matching <see cref="GetBlockState"/>
        /// (so a furnace's facing/lit appearance is driven straight from the pack's blockstate file); otherwise
        /// the single <see cref="Model"/> with <see cref="GetModelTransform"/>.</summary>
        public virtual (BlockModel Model, Matrix4X4<float> Orient) GetRenderModel(WorldBase world, Vector3D<int> blockPos)
        {
            if (StateDefinition != null)
            {
                var variant = StateDefinition.Resolve(GetBlockState(world, blockPos));
                if (variant != null) return (variant.Model, variant.Rotation);
            }
            return (Model, GetModelTransform(world, blockPos));
        }

        /// <summary>Client load step (single-threaded, before any meshing): parse this block's
        /// <see cref="ModelPath"/> into <see cref="Model"/> and its <see cref="BlockStateId"/> into
        /// <see cref="StateDefinition"/> from the resource pack. The headless server never calls it, so it
        /// reads no render assets and needs no resource pack. Override to build a model some other way.</summary>
        public virtual void LoadModel()
        {
            if (ModelPath != null) Model = ResourceReader.ReadBlockModel(ModelPath);
            if (BlockStateId != null) StateDefinition = ResourceReader.ReadBlockState(BlockStateId);
        }

        /// <summary>True for blocks rendered as a <em>block entity</em> — a separate animated box model drawn by
        /// the client's block-entity renderer (e.g. a chest), not baked into the chunk mesh. The mesher skips
        /// such blocks (they emit no chunk geometry); storage, collision and targeting are unaffected. The model
        /// + texture come from <see cref="BlockEntityModelPath"/>/<see cref="BlockEntityTexturePath"/>, and the
        /// inventory icon + first-person viewmodel use that same box model instead of a chunk-mesh cube.</summary>
        public virtual bool RendersAsBlockEntity => false;

        /// <summary>Resource path of this block's block-entity geometry (a Bedrock <c>*.geo.json</c>), used only
        /// when <see cref="RendersAsBlockEntity"/>. Client-only; the headless server never resolves it.</summary>
        public virtual string BlockEntityModelPath => null;

        /// <summary>Resource identifier of this block-entity's texture (e.g. <c>minecraft:entity/chest/normal</c>),
        /// used only when <see cref="RendersAsBlockEntity"/>. Client-only.</summary>
        public virtual string BlockEntityTexturePath => null;

        /// <summary>The Y rotation (radians) the block-entity model is drawn at, derived from the block's stored
        /// data (e.g. a chest's facing). Client-side; default unrotated.</summary>
        public virtual float GetBlockEntityRotation(WorldBase world, Vector3D<int> blockPos) => 0f;

        /// <summary>True for blocks whose <see cref="OnServerTick"/> must run every server tick (e.g. a smelting
        /// furnace). The server keeps a registry of such block positions so it ticks only them.</summary>
        public virtual bool NeedsServerTick => false;

        /// <summary>Server-authoritative per-tick update for a <see cref="NeedsServerTick"/> block (never called
        /// on the client). Reads/writes the block's stored <see cref="BlockData"/>.</summary>
        public virtual void OnServerTick(WorldServer world, Vector3D<int> blockPos) { }

        /// <summary>Server-side: a block in one of the six face-adjacent positions changed (placed, broken, or
        /// fell). Default no-op. Overriders must stay light — only schedule a tick
        /// (<see cref="WorldServer.ScheduleBlockTick"/>) or touch their own block data; do not call back into
        /// <c>SetBlock</c>, which would recurse through this notification. Falling blocks use it to start falling
        /// when the block beneath them is removed.</summary>
        public virtual void OnNeighborChanged(WorldServer world, Vector3D<int> blockPos, Vector3D<int> changedPos) { }

        public virtual Vector4D<float> GetTintColor(WorldBase world, Vector3D<int> blockPos, int tintId) => new Vector4D<float>(1f, 1f, 1f, 1f);
        public virtual LightLevel GetLightLevel(WorldBase world, Vector3D<int> blockPos) => LightLevel.Zero;

        public virtual void OnPlaced(WorldBase world, Vector3D<int> blockPos, EntityPlayer player, int metadata)
        {
        }

        /// <summary>Server-side: this block is about to be removed by a player break. Default no-op; a container
        /// block (e.g. a chest) overrides it to drop its stored contents so they aren't lost. Called just before
        /// the block is set to air; must not place/break blocks itself.</summary>
        public virtual void OnBroken(WorldServer world, Vector3D<int> blockPos) { }

        /// <summary>Client-side: derive the metadata to carry in the place request (e.g. a stair's facing from
        /// the placing player's look + clicked face). Runs on the client; takes only Core types so the headless
        /// server never depends on input/windowing.</summary>
        public virtual int GetPlacementMetadata(EntityPlayer player, BlockRaytraceResult ray) => 0;

        /// <summary>Client-side: the player right-clicked this block while looking at it. Return true if the
        /// block handled the interaction (e.g. opened a GUI), which suppresses placing the held item. Runs
        /// only on the client (the headless server never calls it), so it may touch window/GUI state via the
        /// client globals (<c>ClientResources.Window</c>, <c>StateEngine</c>) — not passed in, so Core's
        /// signature stays free of windowing types.</summary>
        public virtual bool OnActivated(WorldBase world, Vector3D<int> blockPos, EntityPlayer player) => false;

        public virtual int OnLightPassThrough(WorldBase world, Vector3D<int> blockPos, int lightLevel, int color)
            => lightLevel - 1;

        public virtual string GetUnlocalizedName(WorldBase world, Vector3D<int> blockPos) =>
            MinecraftId != null
                ? Identifier.TranslationKey("block", MinecraftId)
                : I18N.UnlocalizedName(RegistryKey, "blocks");

        public virtual string GetName(WorldBase world, Vector3D<int> blockPos) => I18N.Get(GetUnlocalizedName(world, blockPos));

        public bool IsOpaqueFullBlock(WorldBase world, Vector3D<int> blockPos) =>
            IsVisible(world, blockPos) && IsFullBlock(world, blockPos) &&
            IsTransparent(world, blockPos) == TransparencyType.None;
    }
}

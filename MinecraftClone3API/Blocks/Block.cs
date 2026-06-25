using System.Collections.Generic;
using MinecraftClone3API.Client;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

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
        
        public BlockModel Model = ClientResources.MissingModel;

        public Block(string name) : base(name)
        {
        }

        public ushort Id { get; internal set; }

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

        public virtual Color4 GetTintColor(WorldBase world, Vector3i blockPos, int tintId) => Color4.White;
        public virtual LightLevel GetLightLevel(WorldBase world, Vector3i blockPos) => LightLevel.Zero;

        public virtual void OnPlaced(WorldBase world, Vector3i blockPos, EntityPlayer player, int metadata)
        {
        }

        /// <summary>Client-side: derive the metadata to carry in the place request (e.g. a tint from held
        /// keys, or a stair's facing from the placing player's look + clicked face). Runs on the client so
        /// the server — which may be headless — never reads input.</summary>
        public virtual int GetPlacementMetadata(KeyboardState ks, EntityPlayer player, BlockRaytraceResult ray) => 0;

        /// <summary>Client-side: the player right-clicked this block while looking at it. Return true if the
        /// block handled the interaction (e.g. opened a GUI), which suppresses placing the held item. Runs
        /// only on the client (the headless server never calls it), so it may touch window/GUI state.</summary>
        public virtual bool OnActivated(GameWindow window, WorldBase world, Vector3i blockPos, EntityPlayer player) => false;

        public virtual int OnLightPassThrough(WorldBase world, Vector3i blockPos, int lightLevel, int color)
            => lightLevel - 1;

        public virtual string GetUnlocalizedName(WorldBase world, Vector3i blockPos) =>
            I18N.UnlocalizedName(RegistryKey, "blocks");

        public virtual string GetName(WorldBase world, Vector3i blockPos) => I18N.Get(GetUnlocalizedName(world, blockPos));

        public bool IsOpaqueFullBlock(WorldBase world, Vector3i blockPos) =>
            IsVisible(world, blockPos) && IsFullBlock(world, blockPos) &&
            IsTransparent(world, blockPos) == TransparencyType.None;
    }
}

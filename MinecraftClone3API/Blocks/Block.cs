using MinecraftClone3API.Client;
using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
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

        public virtual ConnectionType ConnectsToBlock(WorldBase world, Vector3i blockPos, Vector3i otherBlockPos,
            Block otherBlock) => ConnectionType.Undefined;

        public virtual bool CanPassThrough(WorldBase world, Vector3i blockPos) => false;
        public virtual bool CanTarget(WorldBase world, Vector3i vector3I) => true;

        public virtual AxisAlignedBoundingBox GetBoundingBox(WorldBase world, Vector3i blockPos)
            => DefaultAlignedBoundingBox;

        public virtual Color4 GetTintColor(WorldBase world, Vector3i blockPos, int tintId) => Color4.White;
        public virtual LightLevel GetLightLevel(WorldBase world, Vector3i blockPos) => LightLevel.Zero;

        public virtual void OnPlaced(WorldBase world, Vector3i blockPos, EntityPlayer player, int metadata)
        {
        }

        /// <summary>Client-side: derive the metadata to carry in the place request (e.g. a tint chosen by
        /// held keys). Runs on the client so the server — which may be headless — never reads input.</summary>
        public virtual int GetPlacementMetadata(KeyboardState ks) => 0;

        public virtual int OnLightPassThrough(WorldBase world, Vector3i blockPos, int lightLevel, int color)
            => lightLevel - 1;

        public virtual string GetUnlocalizedName(WorldBase world, Vector3i blockPos) => Name;

        public virtual string GetName(WorldBase world, Vector3i blockPos) => I18N.Get(GetUnlocalizedName(world, blockPos));

        public bool IsOpaqueFullBlock(WorldBase world, Vector3i blockPos) =>
            IsVisible(world, blockPos) && IsFullBlock(world, blockPos) &&
            IsTransparent(world, blockPos) == TransparencyType.None;
    }
}

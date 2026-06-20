using MinecraftClone3API.Client;
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

        public virtual AxisAlignedBoundingBox GetBoundingBox(WorldBase world, Vector3i blockPos)
            => DefaultAlignedBoundingBox;

        public virtual Color4 GetTintColor(WorldBase world, Vector3i blockPos, int tintId) => Color4.White;
        public virtual LightLevel GetLightLevel(WorldBase world, Vector3i blockPos) => LightLevel.Zero;

        public virtual void OnPlaced(WorldBase world, Vector3i blockPos, EntityPlayer player)
        {
        }

        public virtual int OnLightPassThrough(WorldBase world, Vector3i blockPos, int lightLevel, int color)
            => lightLevel - 1;

        public virtual string GetUnlocalizedName(WorldBase world, Vector3i blockPos) => Name;

        public virtual string GetName(WorldBase world, Vector3i blockPos) => I18N.Get(GetUnlocalizedName(world, blockPos));

        public bool IsOpaqueFullBlock(WorldBase world, Vector3i blockPos) =>
            IsVisible(world, blockPos) && IsFullBlock(world, blockPos) &&
            IsTransparent(world, blockPos) == TransparencyType.None;
    }
}

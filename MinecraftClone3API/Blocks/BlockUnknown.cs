namespace MinecraftClone3API.Blocks
{
    /// <summary>
    /// An inert placeholder for a saved block whose registry name no longer resolves — its plugin is absent
    /// or it was renamed. Minted on demand by <see cref="Util.BlockRegistry.GetOrRegisterUnknown"/>, one
    /// instance per distinct missing name, keeping that name as its <see cref="RegistryEntry.RegistryKey"/>
    /// so the cell round-trips losslessly: re-installing the plugin makes the name resolve again and the
    /// original block returns untouched. Renders as the default missing-texture cube (never gets a client
    /// <see cref="Block.LoadModel"/> pass, registered after it) and behaves as a plain solid block.
    /// </summary>
    public sealed class BlockUnknown : Block
    {
        public BlockUnknown(string registryKey) : base(registryKey)
        {
        }
    }
}

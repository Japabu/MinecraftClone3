namespace MinecraftClone3API.Items
{
    /// <summary>
    /// An inert placeholder for a saved item whose registry name no longer resolves — its plugin is absent
    /// or it was renamed. Minted on demand by <see cref="Util.ItemRegistry.GetOrRegisterUnknown"/>, one
    /// instance per distinct missing name, keeping that name as its <see cref="RegistryEntry.RegistryKey"/>
    /// so the stack round-trips losslessly: re-installing the plugin makes the name resolve again and the
    /// original item returns untouched.
    /// </summary>
    public sealed class ItemUnknown : Item
    {
        public ItemUnknown(string registryKey) : base(registryKey)
        {
        }
    }
}

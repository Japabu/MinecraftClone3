using System.Collections.Concurrent;
using MinecraftClone3API.Blocks;

namespace MinecraftClone3API.Util
{
    /// <summary>
    /// Registry of <see cref="Block"/>s. Assigns each a runtime numeric <see cref="Block.Id"/> in
    /// registration order. The id is a session-local detail only: chunks persist a block's stable
    /// <see cref="RegistryEntry.RegistryKey"/> (name), not its id, so adding/removing/reordering blocks never
    /// corrupts a saved world. A saved name that no longer resolves is preserved as an inert
    /// <see cref="BlockUnknown"/> via <see cref="GetOrRegisterUnknown"/>.
    /// </summary>
    public class BlockRegistry : Registry<Block>
    {
        public static readonly Block BlockAir = new BlockAir();

        // Concurrent + a register lock: placeholder blocks are minted on the chunk load/apply thread while
        // the mesher/render threads read these maps. Reads (the hot id->block lookup) stay lock-free.
        private readonly ConcurrentDictionary<ushort, Block> _idsToBlocks = new ConcurrentDictionary<ushort, Block>();
        private readonly ConcurrentDictionary<string, ushort> _keysToIds = new ConcurrentDictionary<string, ushort>();
        private readonly object _registerLock = new object();

        public BlockRegistry()
        {
            Register("System", BlockAir);
        }

        public Block this[ushort id] => _idsToBlocks[id];

        public sealed override void Register(string prefix, Block block)
        {
            base.Register(prefix, block);
            lock (_registerLock) AssignId(block);
        }

        /// <summary>Resolves a saved block name to a runtime id. If the name isn't registered (its plugin is
        /// absent or it was renamed) mints an inert <see cref="BlockUnknown"/> that keeps the name verbatim,
        /// so the cell round-trips losslessly and re-installing the plugin restores the real block.</summary>
        public ushort GetOrRegisterUnknown(string registryKey)
        {
            if (_keysToIds.TryGetValue(registryKey, out var id)) return id;
            lock (_registerLock)
            {
                if (_keysToIds.TryGetValue(registryKey, out id)) return id;
                var block = new BlockUnknown(registryKey);
                RegisterWithKey(registryKey, block);
                AssignId(block);
                return block.Id;
            }
        }

        private void AssignId(Block block)
        {
            block.Id = GetBlockId(block);
            _idsToBlocks[block.Id] = block;
            _keysToIds[block.RegistryKey] = block.Id;
        }

        private ushort GetBlockId(Block block)
        {
            if (_keysToIds.TryGetValue(block.RegistryKey, out var id)) return id;

            id = 0;
            while (_idsToBlocks.ContainsKey(id)) id++;
            return id;
        }
    }
}

using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;

namespace VanillaPlugin.Blocks
{
    public class BlockBasic : Block
    {
        private readonly bool _fullBlock;

        public BlockBasic(string name, string modelPath, bool fullBlock) : base(name)
        {
            _fullBlock = fullBlock;

            MinecraftId = Identifier.FromResourcePath(modelPath);
            Model = ResourceReader.ReadBlockModel(modelPath);
        }

        public override bool IsFullBlock(WorldBase world, Vector3i blockPos) => _fullBlock;
    }
}

using MinecraftClone3API.Blocks;
using MinecraftClone3API.IO;
using MinecraftClone3API.Util;

namespace VanillaPlugin.Blocks
{
    public class BlockBasic : Block
    {
        private readonly bool _fullBlock;
        private readonly float _hardness;

        public BlockBasic(string name, string modelPath, bool fullBlock, float hardness = 1.5f) : base(name)
        {
            _fullBlock = fullBlock;
            _hardness = hardness;

            MinecraftId = Identifier.FromResourcePath(modelPath);
            Model = ResourceReader.ReadBlockModel(modelPath);
        }

        public override bool IsFullBlock(WorldBase world, Vector3i blockPos) => _fullBlock;
        public override float Hardness => _hardness;
    }
}

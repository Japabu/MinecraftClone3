using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace VanillaPlugin.Blocks
{
    /// <summary>A full block that emits bright white light, like a torch but as a solid cube.</summary>
    public class BlockGlowstone : BlockBasic
    {
        public BlockGlowstone() : base("Glowstone", "minecraft:block/glowstone", true)
        {
        }

        public override LightLevel GetLightLevel(WorldBase world, Vector3D<int> blockPos) => new LightLevel(15, 15, 15);
    }
}

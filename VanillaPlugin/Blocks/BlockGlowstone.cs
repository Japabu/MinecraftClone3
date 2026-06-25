using MinecraftClone3API.Blocks;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace VanillaPlugin.Blocks
{
    /// <summary>A full block that emits bright white light, like a torch but as a solid cube.</summary>
    public class BlockGlowstone : BlockBasic
    {
        public BlockGlowstone() : base("Glowstone", "minecraft:block/glowstone", true)
        {
        }

        public override LightLevel GetLightLevel(WorldBase world, Vector3i blockPos) => new LightLevel(15, 15, 15);
    }
}

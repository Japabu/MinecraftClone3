using MinecraftClone3API.Util;
using MinecraftClone3API.WorldGen;
using OpenTK.Mathematics;

namespace VanillaPlugin.WorldGen
{
    /// <summary>
    /// The vanilla Nether: a sealed netherrack slab with lava seas, reached through obsidian portals from the
    /// Overworld (see <see cref="VanillaPortals"/>). Wires a <see cref="NetherChunkGenerator"/> from the
    /// registered Nether blocks. Registry key <see cref="Key"/>.
    /// </summary>
    public class NetherDimension : Dimension
    {
        public const string Key = "Vanilla:Nether";

        public NetherDimension() : base("Nether")
        {
            HasSky = false;
            FogColor = new Vector3(0.18f, 0.03f, 0.02f);
            AmbientLight = new Vector3(0.13f, 0.06f, 0.05f);
        }

        public override IChunkGenerator CreateGenerator(long seed)
        {
            var netherrack = GameRegistry.GetBlock("Vanilla:Netherrack");
            var lava = GameRegistry.GetBlock("Vanilla:Lava");
            var bedrock = GameRegistry.GetBlock("Vanilla:Bedrock");
            var soulSand = GameRegistry.GetBlock("Vanilla:SoulSand");
            var glowstone = GameRegistry.GetBlock("Vanilla:Glowstone");

            return new NetherChunkGenerator(seed, netherrack, lava, bedrock, soulSand, glowstone);
        }
    }
}

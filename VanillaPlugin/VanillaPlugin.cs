using MinecraftClone3API.Plugins;
using VanillaPlugin.BlockDatas;
using VanillaPlugin.Blocks;

namespace VanillaPlugin
{
    [Plugin("Vanilla", "1.0", "Vanilla")]
    public class VanillaPlugin : IPlugin
    {
        public void LoadResources(PluginContext context)
        {
            //System.Threading.Thread.Sleep(4000);
        }

        public void PreLoad(PluginContext context)
        {
        }

        public void Load(PluginContext context)
        {
            context.Register(new BlockBasic("Stone", "Vanilla/Models/Stone.json", true));
            context.Register(new BlockBasic("Dirt", "Vanilla/Models/Dirt.json", true));
            context.Register(new BlockBasic("DirtStairs", "Vanilla/Models/DirtStairs.json", false));
            context.Register(new BlockBasic("BrewingStand", "Vanilla/Models/BrewingStand.json", false));

            context.Register(new BlockGrass());
            context.Register(new BlockTorch());
            //context.Register(new BlockGlass());
            //context.Register(new BlockTintedGlass());

            context.Register<BlockDataMetadata>();
        }

        public void PostLoad(PluginContext context)
        {
        }

        public void Unload(PluginContext context)
        {
        }
    }
}

using MinecraftClone3API.Plugins;
using VanillaPlugin.BlockDatas;
using VanillaPlugin.Blocks;
using VanillaPlugin.WorldGen;

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
            context.Register(new BlockBasic("Stone", "minecraft:block/stone", true));
            context.Register(new BlockBasic("Dirt", "minecraft:block/dirt", true));
            context.Register(new BlockBasic("Sand", "minecraft:block/sand", true));
            context.Register(new BlockBasic("Gravel", "minecraft:block/gravel", true));
            context.Register(new BlockBasic("Snow", "minecraft:block/snow_block", true));
            context.Register(new BlockBasic("Bedrock", "minecraft:block/bedrock", true));
            context.Register(new BlockBasic("CoalOre", "minecraft:block/coal_ore", true));
            context.Register(new BlockBasic("IronOre", "minecraft:block/iron_ore", true));
            context.Register(new BlockBasic("GoldOre", "minecraft:block/gold_ore", true));
            context.Register(new BlockBasic("DiamondOre", "minecraft:block/diamond_ore", true));
            context.Register(new BlockBasic("OakLog", "minecraft:block/oak_log", true));
            context.Register(new BlockLeaves());
            context.Register(new BlockWater());
            context.Register(new BlockBasic("BrewingStand", "minecraft:block/brewing_stand", false));

            context.Register(new BlockGrass());
            context.Register(new BlockTorch());
            context.Register(new BlockStairs());
            //context.Register(new BlockGlass());
            //context.Register(new BlockTintedGlass());

            context.Register<BlockDataMetadata>();

            VanillaWorldGen.Register(context);
            context.Register(new OverworldDimension());
        }

        public void PostLoad(PluginContext context)
        {
            VanillaWorldGen.AttachShared();
        }

        public void Unload(PluginContext context)
        {
        }
    }
}

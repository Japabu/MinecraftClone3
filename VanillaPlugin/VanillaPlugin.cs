using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Plugins;
using VanillaPlugin.BlockDatas;
using VanillaPlugin.Blocks;
using VanillaPlugin.Items;
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
            context.Register(new BlockBasic("Cobblestone", "minecraft:block/cobblestone", true));
            context.Register(new BlockBasic("Dirt", "minecraft:block/dirt", true));
            context.Register(new BlockBasic("Sand", "minecraft:block/sand", true));
            context.Register(new BlockBasic("Gravel", "minecraft:block/gravel", true));
            context.Register(new BlockBasic("Snow", "minecraft:block/snow_block", true));
            context.Register(new BlockBasic("Bedrock", "minecraft:block/bedrock", true));
            context.Register(new BlockBasic("Obsidian", "minecraft:block/obsidian", true));
            context.Register(new BlockBasic("Bricks", "minecraft:block/bricks", true));
            context.Register(new BlockBasic("StoneBricks", "minecraft:block/stone_bricks", true));
            context.Register(new BlockBasic("CoalOre", "minecraft:block/coal_ore", true));
            context.Register(new BlockBasic("IronOre", "minecraft:block/iron_ore", true));
            context.Register(new BlockBasic("GoldOre", "minecraft:block/gold_ore", true));
            context.Register(new BlockBasic("DiamondOre", "minecraft:block/diamond_ore", true));
            context.Register(new BlockBasic("OakLog", "minecraft:block/oak_log", true));
            context.Register(new BlockBasic("OakPlanks", "minecraft:block/oak_planks", true));
            context.Register(new BlockLeaves());
            context.Register(new BlockWater());
            context.Register(new BlockBasic("BrewingStand", "minecraft:block/brewing_stand", false));

            context.Register(new BlockGrass());
            context.Register(new BlockTorch());
            context.Register(new BlockStairs());
            context.Register(new BlockCraftingTable());
            context.Register(new BlockGlowstone());
            // BlockGlass stays disabled: this resource pack's block/glass.json stores textures.all as an
            // object, which BlockModel.Parse can't read (the reason it was never enabled).
            //context.Register(new BlockGlass());
            //context.Register(new BlockTintedGlass());

            // Standalone (non-placeable) items, rendered from their 2D resource-pack sprites.
            context.Register(new ItemSimple("Stick", "minecraft/textures/item/stick.png"));
            context.Register(new ItemSimple("Coal", "minecraft/textures/item/coal.png"));
            context.Register(new ItemSimple("IronIngot", "minecraft/textures/item/iron_ingot.png"));
            context.Register(new ItemSimple("GoldIngot", "minecraft/textures/item/gold_ingot.png"));
            context.Register(new ItemSimple("Diamond", "minecraft/textures/item/diamond.png"));
            context.Register(new ItemSimple("Apple", "minecraft/textures/item/apple.png"));

            VanillaRecipes.Register(context);

            context.Register<BlockDataMetadata>();

            VanillaWorldGen.Register(context);
            context.Register(new OverworldDimension());

            RegisterEntities(context);
        }

        // Animals, a hostile mob, and the dropped-item type. Width/height drive server collision; the texture
        // paths + box models drive client rendering (the official Minecraft entity sheets from the resource pack).
        private static void RegisterEntities(PluginContext context)
        {
            context.Register(new EntityType("Pig", EntityKind.Creature, 0.9f, 0.9f, 10f, 0.1f, false,
                "minecraft:entity/pig/pig", EntityModels.Pig));
            context.Register(new EntityType("Cow", EntityKind.Creature, 0.9f, 1.4f, 10f, 0.1f, false,
                "minecraft:entity/cow/cow", EntityModels.Cow));
            context.Register(new EntityType("Sheep", EntityKind.Creature, 0.9f, 1.3f, 8f, 0.1f, false,
                "minecraft:entity/sheep/sheep", EntityModels.Sheep));
            context.Register(new EntityType("Chicken", EntityKind.Creature, 0.4f, 0.7f, 4f, 0.08f, false,
                "minecraft:entity/chicken/chicken", EntityModels.Chicken));
            context.Register(new EntityType("Zombie", EntityKind.Creature, 0.6f, 1.95f, 20f, 0.13f, true,
                "minecraft:entity/zombie/zombie", EntityModels.Biped));

            context.Register(new EntityType("Item", EntityKind.Item, 0.25f, 0.25f, 1f, 0f, false, null, null));
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

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
            context.Register(new BlockFurnace());
            context.Register(new BlockGlowstone());

            // Nether content.
            context.Register(new BlockBasic("Netherrack", "minecraft:block/netherrack", true));
            context.Register(new BlockBasic("SoulSand", "minecraft:block/soul_sand", true));
            context.Register(new BlockBasic("NetherQuartzOre", "minecraft:block/nether_quartz_ore", true));
            context.Register(new BlockLava());
            context.Register(new BlockNetherPortal());
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

            context.Register<BlockDataMetadata>();
            context.Register<BlockDataFurnace>();

            VanillaWorldGen.Register(context);
            context.Register(new OverworldDimension());
            context.Register(new NetherDimension());

            // Obsidian-portal Overworld↔Nether travel, and the flint &amp; steel that lights a frame.
            var portals = new VanillaPortals();
            context.RegisterPortals(portals);
            context.Register(new ItemFlintAndSteel(portals));

            RegisterEntities(context);
        }

        // Animals, a hostile mob, and the dropped-item type. Width/height drive server collision; the texture
        // paths + Bedrock geometry files drive client rendering (the official Minecraft entity sheets —
        // pig/cow/chicken carry the climate-variant suffix used by current Minecraft; the zombie reuses the
        // shared humanoid model from the System plugin). Each creature also gets a creative spawn egg (its
        // official 2D item sprite) that spawns it on right-click for testing.
        private static void RegisterEntities(PluginContext context)
        {
            var pig = new EntityType("Pig", EntityKind.Creature, 0.9f, 0.9f, 10f, 0.1f, false,
                "minecraft:entity/pig/pig_temperate", "Vanilla/Models/Entity/pig.geo.json");
            var cow = new EntityType("Cow", EntityKind.Creature, 0.9f, 1.4f, 10f, 0.1f, false,
                "minecraft:entity/cow/cow_temperate", "Vanilla/Models/Entity/cow.geo.json");
            var sheep = new EntityType("Sheep", EntityKind.Creature, 0.9f, 1.3f, 8f, 0.1f, false,
                "minecraft:entity/sheep/sheep", "Vanilla/Models/Entity/sheep.geo.json");
            var chicken = new EntityType("Chicken", EntityKind.Creature, 0.4f, 0.7f, 4f, 0.08f, false,
                "minecraft:entity/chicken/chicken_temperate", "Vanilla/Models/Entity/chicken.geo.json");
            var zombie = new EntityType("Zombie", EntityKind.Creature, 0.6f, 1.95f, 20f, 0.13f, true,
                "minecraft:entity/zombie/zombie", "System/Models/Entity/biped.geo.json");

            context.Register(pig);
            context.Register(cow);
            context.Register(sheep);
            context.Register(chicken);
            context.Register(zombie);
            context.Register(new EntityType("Item", EntityKind.Item, 0.25f, 0.25f, 1f, 0f, false, null, null));

            context.Register(new ItemSpawnEgg(pig, "minecraft/textures/item/pig_spawn_egg.png"));
            context.Register(new ItemSpawnEgg(cow, "minecraft/textures/item/cow_spawn_egg.png"));
            context.Register(new ItemSpawnEgg(sheep, "minecraft/textures/item/sheep_spawn_egg.png"));
            context.Register(new ItemSpawnEgg(chicken, "minecraft/textures/item/chicken_spawn_egg.png"));
            context.Register(new ItemSpawnEgg(zombie, "minecraft/textures/item/zombie_spawn_egg.png"));
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

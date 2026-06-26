using MinecraftClone3API.Entities;
using MinecraftClone3API.Graphics;
using MinecraftClone3API.Items;
using MinecraftClone3API.Plugins;
using VanillaPlugin.BlockDatas;
using VanillaPlugin.Blocks;
using VanillaPlugin.Entities;
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
            // Hardness, preferred tool, and harvest tier match Minecraft (drive survival mining time); bedrock
            // is unbreakable (-1). Stone/ores/obsidian require the correct tool tier to mine at full speed.
            context.Register(new BlockBasic("Stone", "minecraft:block/stone", true, 1.5f, ToolType.Pickaxe, 0, true));
            context.Register(new BlockBasic("Cobblestone", "minecraft:block/cobblestone", true, 2.0f, ToolType.Pickaxe, 0, true));
            context.Register(new BlockBasic("Dirt", "minecraft:block/dirt", true, 0.5f, ToolType.Shovel));
            context.Register(new BlockFalling("Sand", "minecraft:block/sand", 0.5f, ToolType.Shovel));
            context.Register(new BlockFalling("Gravel", "minecraft:block/gravel", 0.6f, ToolType.Shovel));
            context.Register(new BlockBasic("Snow", "minecraft:block/snow_block", true, 0.2f, ToolType.Shovel, 0, true));
            context.Register(new BlockBasic("Bedrock", "minecraft:block/bedrock", true, -1f));
            context.Register(new BlockBasic("Obsidian", "minecraft:block/obsidian", true, 50.0f, ToolType.Pickaxe, 3, true));
            context.Register(new BlockBasic("Bricks", "minecraft:block/bricks", true, 2.0f, ToolType.Pickaxe, 0, true));
            context.Register(new BlockBasic("StoneBricks", "minecraft:block/stone_bricks", true, 1.5f, ToolType.Pickaxe, 0, true));
            context.Register(new BlockBasic("CoalOre", "minecraft:block/coal_ore", true, 3.0f, ToolType.Pickaxe, 0, true));
            context.Register(new BlockBasic("IronOre", "minecraft:block/iron_ore", true, 3.0f, ToolType.Pickaxe, 1, true));
            context.Register(new BlockBasic("GoldOre", "minecraft:block/gold_ore", true, 3.0f, ToolType.Pickaxe, 2, true));
            context.Register(new BlockBasic("DiamondOre", "minecraft:block/diamond_ore", true, 3.0f, ToolType.Pickaxe, 2, true));
            context.Register(new BlockBasic("OakLog", "minecraft:block/oak_log", true, 2.0f, ToolType.Axe));
            context.Register(new BlockBasic("OakPlanks", "minecraft:block/oak_planks", true, 2.0f, ToolType.Axe));
            context.Register(new BlockLeaves());
            context.Register(new BlockWater());
            context.Register(new BlockBasic("BrewingStand", "minecraft:block/brewing_stand", false));

            context.Register(new BlockGrass());
            context.Register(new BlockTorch());
            context.Register(new BlockStairs());
            context.Register(new BlockCraftingTable());
            context.Register(new BlockFurnace());
            context.Register(new BlockGlowstone());
            context.Register(new BlockBasic("WhiteWool", "minecraft:block/white_wool", true));

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
            context.Register(new ItemFood("Apple", "minecraft/textures/item/apple.png", 4f, 0.3f));
            context.Register(new ItemShears());

            // Mining tools per Minecraft material (speed multiplier, harvest tier): wood 2/0, stone 4/1,
            // iron 6/2, gold 12/0, diamond 8/3. Each material gets a pickaxe, axe, and shovel.
            RegisterToolSet(context, "Wooden", "wooden", 2f, 0);
            RegisterToolSet(context, "Stone", "stone", 4f, 1);
            RegisterToolSet(context, "Iron", "iron", 6f, 2);
            RegisterToolSet(context, "Golden", "golden", 12f, 0);
            RegisterToolSet(context, "Diamond", "diamond", 8f, 3);

            context.Register<BlockDataMetadata>();
            context.Register<BlockDataFurnace>();

            context.RegisterEntityData<FallingBlockData>();

            VanillaWorldGen.Register(context);
            context.Register(new OverworldDimension());
            context.Register(new NetherDimension());

            // Obsidian-portal Overworld↔Nether travel, and the flint &amp; steel that lights a frame.
            var portals = new VanillaPortals();
            context.RegisterPortals(portals);
            context.Register(new ItemFlintAndSteel(portals));

            RegisterEntities(context);
        }

        private static void RegisterToolSet(PluginContext context, string prefix, string material, float speed, int tier)
        {
            context.Register(new ItemTool(prefix + "Pickaxe", $"minecraft/textures/item/{material}_pickaxe.png", ToolType.Pickaxe, speed, tier));
            context.Register(new ItemTool(prefix + "Axe", $"minecraft/textures/item/{material}_axe.png", ToolType.Axe, speed, tier));
            context.Register(new ItemTool(prefix + "Shovel", $"minecraft/textures/item/{material}_shovel.png", ToolType.Shovel, speed, tier));
        }

        // Animals, a hostile mob, and the dropped-item type. Width/height drive server collision; the texture
        // paths + Bedrock geometry files drive client rendering (the official Minecraft entity sheets —
        // pig/cow/chicken carry the climate-variant suffix used by current Minecraft; the zombie reuses the
        // shared humanoid model from the System plugin). Each creature also gets a creative spawn egg (its
        // official 2D item sprite) that spawns it on right-click for testing.
        private static void RegisterEntities(PluginContext context)
        {
            context.RegisterEntityData<SheepData>();

            var pig = new EntityType("Pig", EntityKind.Creature, 0.9f, 0.9f, 10f, 0.1f, false,
                "minecraft:entity/pig/pig_temperate", "Vanilla/Models/Entity/pig.geo.json");
            var cow = new EntityType("Cow", EntityKind.Creature, 0.9f, 1.4f, 10f, 0.1f, false,
                "minecraft:entity/cow/cow_temperate", "Vanilla/Models/Entity/cow.geo.json");
            // The sheep carries a wool overlay (its own texture) that its SheepData hides once sheared.
            var sheep = new EntityType("Sheep", EntityKind.Creature, 0.9f, 1.3f, 8f, 0.1f, false,
                "minecraft:entity/sheep/sheep", "Vanilla/Models/Entity/sheep.geo.json",
                "Vanilla/Models/Entity/sheep_wool.geo.json", "minecraft:entity/sheep/sheep_wool",
                () => new SheepData());
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
            context.Register(new EntityType("FallingBlock", EntityKind.FallingBlock,
                EntityFallingBlock.Size, EntityFallingBlock.Size, 1f, 0f, false, null, null));

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

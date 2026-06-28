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

        // The 16 Minecraft dye colours, used for both stained glass and wool (resource ids share the prefix).
        private static readonly string[] DyeColors =
        {
            "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray",
            "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black"
        };

        // Wood types beyond oak (which is registered explicitly). Each gets a log, planks, and leaves set.
        private static readonly string[] WoodTypes = { "spruce", "birch", "jungle", "acacia", "dark_oak" };

        public void Load(PluginContext context)
        {
            // Hardness, preferred tool, and harvest tier match Minecraft (drive survival mining time); bedrock
            // is unbreakable (-1). Stone/ores/obsidian require the correct tool tier to mine at full speed.
            context.Register(new BlockBasic("Stone", "minecraft:block/stone", true, 1.5f, ToolType.Pickaxe, 0, true));
            context.Register(new BlockBasic("Cobblestone", "minecraft:block/cobblestone", true, 2.0f, ToolType.Pickaxe, 0, true));
            context.Register(new BlockBasic("Dirt", "minecraft:block/dirt", true, 0.5f, ToolType.Shovel) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockFalling("Sand", "minecraft:block/sand", 0.5f, ToolType.Shovel));
            context.Register(new BlockFalling("Gravel", "minecraft:block/gravel", 0.6f, ToolType.Shovel));
            context.Register(new BlockBasic("Snow", "minecraft:block/snow_block", true, 0.2f, ToolType.Shovel, 0, true) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("Bedrock", "minecraft:block/bedrock", true, -1f));
            context.Register(new BlockBasic("Obsidian", "minecraft:block/obsidian", true, 50.0f, ToolType.Pickaxe, 3, true));
            context.Register(new BlockBasic("Bricks", "minecraft:block/bricks", true, 2.0f, ToolType.Pickaxe, 0, true));
            context.Register(new BlockBasic("StoneBricks", "minecraft:block/stone_bricks", true, 1.5f, ToolType.Pickaxe, 0, true));
            context.Register(new BlockBasic("CoalOre", "minecraft:block/coal_ore", true, 3.0f, ToolType.Pickaxe, 0, true) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("IronOre", "minecraft:block/iron_ore", true, 3.0f, ToolType.Pickaxe, 1, true) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("GoldOre", "minecraft:block/gold_ore", true, 3.0f, ToolType.Pickaxe, 2, true) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("DiamondOre", "minecraft:block/diamond_ore", true, 3.0f, ToolType.Pickaxe, 2, true) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("OakLog", "minecraft:block/oak_log", true, 2.0f, ToolType.Axe) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("OakPlanks", "minecraft:block/oak_planks", true, 2.0f, ToolType.Axe));
            context.Register(new BlockLeaves("Leaves", "minecraft:block/oak_leaves"));
            context.Register(new BlockWater());
            context.Register(new BlockBasic("BrewingStand", "minecraft:block/brewing_stand", false) { CreativeTab = CreativeTab.FunctionalBlocks });

            // Wood sets for the other tree species (log/planks/leaves), so forests and crafting have variety.
            foreach (var wood in WoodTypes)
                RegisterWoodSet(context, wood);

            // Stone family + decorative building blocks; all mined with a pickaxe.
            RegisterStone(context, "Granite", "granite");
            RegisterStone(context, "PolishedGranite", "polished_granite");
            RegisterStone(context, "Diorite", "diorite");
            RegisterStone(context, "PolishedDiorite", "polished_diorite");
            RegisterStone(context, "Andesite", "andesite");
            RegisterStone(context, "PolishedAndesite", "polished_andesite");
            RegisterStone(context, "MossyCobblestone", "mossy_cobblestone");
            RegisterStone(context, "Sandstone", "sandstone");
            RegisterStone(context, "RedSandstone", "red_sandstone");
            RegisterStone(context, "SmoothStone", "smooth_stone");
            RegisterStone(context, "Deepslate", "deepslate");
            RegisterStone(context, "Tuff", "tuff");
            RegisterStone(context, "Calcite", "calcite");
            RegisterStone(context, "NetherBricks", "nether_bricks");
            RegisterStone(context, "QuartzBlock", "quartz_block");
            RegisterStone(context, "Prismarine", "prismarine");
            RegisterStone(context, "Terracotta", "terracotta");
            context.Register(new BlockBasic("Clay", "minecraft:block/clay", true, 0.6f, ToolType.Shovel) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("Bookshelf", "minecraft:block/bookshelf", true, 1.5f, ToolType.Axe) { CreativeTab = CreativeTab.FunctionalBlocks });
            context.Register(new BlockBasic("HayBlock", "minecraft:block/hay_block", true, 0.5f));
            context.Register(new BlockBasic("Pumpkin", "minecraft:block/pumpkin", true, 1.0f, ToolType.Axe) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("Melon", "minecraft:block/melon", true, 1.0f, ToolType.Axe) { CreativeTab = CreativeTab.NaturalBlocks });

            // Coloured terracotta, one per dye colour.
            foreach (var color in DyeColors)
                RegisterStone(context, ToPascal(color) + "Terracotta", color + "_terracotta", CreativeTab.ColoredBlocks);

            // Additional ores (tier gates which tool can harvest them; redstone/lapis/copper need stone+).
            context.Register(new BlockBasic("RedstoneOre", "minecraft:block/redstone_ore", true, 3.0f, ToolType.Pickaxe, 2, true) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("LapisOre", "minecraft:block/lapis_ore", true, 3.0f, ToolType.Pickaxe, 1, true) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("EmeraldOre", "minecraft:block/emerald_ore", true, 3.0f, ToolType.Pickaxe, 2, true) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("CopperOre", "minecraft:block/copper_ore", true, 3.0f, ToolType.Pickaxe, 1, true) { CreativeTab = CreativeTab.NaturalBlocks });

            context.Register(new BlockGrass());
            context.Register(new BlockTorch());
            context.Register(new BlockStairs());
            context.Register(new BlockCraftingTable());
            context.Register(new BlockFurnace());
            context.Register(new BlockChest());
            context.Register(new BlockGlowstone());
            context.Register(new BlockBasic("WhiteWool", "minecraft:block/white_wool", true, 0.8f) { CreativeTab = CreativeTab.ColoredBlocks });
            context.Register(new BlockGlass());

            // One stained-glass + one wool block per Minecraft dye colour; each auto-gets a creative item via
            // its ItemBlock. White wool is registered explicitly above (worldgen/shears reference it by key).
            foreach (var color in DyeColors)
            {
                context.Register(new BlockStainedGlass(color));
                if (color != "white")
                    context.Register(new BlockBasic(ToPascal(color) + "Wool", "minecraft:block/" + color + "_wool", true, 0.8f) { CreativeTab = CreativeTab.ColoredBlocks });
            }

            // Nether content.
            context.Register(new BlockBasic("Netherrack", "minecraft:block/netherrack", true) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("SoulSand", "minecraft:block/soul_sand", true) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic("NetherQuartzOre", "minecraft:block/nether_quartz_ore", true) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockLava());
            context.Register(new BlockNetherPortal());

            // Standalone (non-placeable) items, rendered from their 2D resource-pack sprites.
            context.Register(new ItemSimple("Stick", "minecraft/textures/item/stick.png"));
            context.Register(new ItemSimple("Coal", "minecraft/textures/item/coal.png"));
            context.Register(new ItemSimple("Charcoal", "minecraft/textures/item/charcoal.png"));
            context.Register(new ItemSimple("IronIngot", "minecraft/textures/item/iron_ingot.png"));
            context.Register(new ItemSimple("GoldIngot", "minecraft/textures/item/gold_ingot.png"));
            context.Register(new ItemSimple("Diamond", "minecraft/textures/item/diamond.png"));
            context.Register(new ItemSimple("Emerald", "minecraft/textures/item/emerald.png"));
            context.Register(new ItemSimple("LapisLazuli", "minecraft/textures/item/lapis_lazuli.png"));
            context.Register(new ItemSimple("Redstone", "minecraft/textures/item/redstone.png"));
            context.Register(new ItemSimple("IronNugget", "minecraft/textures/item/iron_nugget.png"));
            context.Register(new ItemSimple("GoldNugget", "minecraft/textures/item/gold_nugget.png"));
            context.Register(new ItemSimple("Flint", "minecraft/textures/item/flint.png"));
            context.Register(new ItemSimple("ClayBall", "minecraft/textures/item/clay_ball.png"));
            context.Register(new ItemSimple("Brick", "minecraft/textures/item/brick.png"));
            context.Register(new ItemSimple("Paper", "minecraft/textures/item/paper.png"));
            context.Register(new ItemSimple("Book", "minecraft/textures/item/book.png"));
            context.Register(new ItemSimple("Wheat", "minecraft/textures/item/wheat.png"));
            context.Register(new ItemSimple("Sugar", "minecraft/textures/item/sugar.png"));
            context.Register(new ItemSimple("Bone", "minecraft/textures/item/bone.png"));
            context.Register(new ItemSimple("Feather", "minecraft/textures/item/feather.png"));
            context.Register(new ItemSimple("Leather", "minecraft/textures/item/leather.png"));
            context.Register(new ItemSimple("String", "minecraft/textures/item/string.png"));
            context.Register(new ItemSimple("Gunpowder", "minecraft/textures/item/gunpowder.png"));
            context.Register(new ItemSimple("RottenFlesh", "minecraft/textures/item/rotten_flesh.png"));

            // Food (nutrition, saturation modifier — Minecraft values). Cooked variants are smelted from raw.
            context.Register(new ItemFood("Apple", "minecraft/textures/item/apple.png", 4f, 0.3f));
            context.Register(new ItemFood("Bread", "minecraft/textures/item/bread.png", 5f, 0.6f));
            context.Register(new ItemFood("Cookie", "minecraft/textures/item/cookie.png", 2f, 0.1f));
            context.Register(new ItemFood("MelonSlice", "minecraft/textures/item/melon_slice.png", 2f, 0.3f));
            context.Register(new ItemFood("Carrot", "minecraft/textures/item/carrot.png", 3f, 0.6f));
            context.Register(new ItemFood("Potato", "minecraft/textures/item/potato.png", 1f, 0.3f));
            context.Register(new ItemFood("BakedPotato", "minecraft/textures/item/baked_potato.png", 5f, 0.6f));
            context.Register(new ItemFood("Beef", "minecraft/textures/item/beef.png", 3f, 0.3f));
            context.Register(new ItemFood("CookedBeef", "minecraft/textures/item/cooked_beef.png", 8f, 0.8f));
            context.Register(new ItemFood("Porkchop", "minecraft/textures/item/porkchop.png", 3f, 0.3f));
            context.Register(new ItemFood("CookedPorkchop", "minecraft/textures/item/cooked_porkchop.png", 8f, 0.8f));
            context.Register(new ItemFood("Chicken", "minecraft/textures/item/chicken.png", 2f, 0.3f));
            context.Register(new ItemFood("CookedChicken", "minecraft/textures/item/cooked_chicken.png", 6f, 0.6f));
            context.Register(new ItemFood("Mutton", "minecraft/textures/item/mutton.png", 2f, 0.3f));
            context.Register(new ItemFood("CookedMutton", "minecraft/textures/item/cooked_mutton.png", 6f, 0.8f));
            context.Register(new ItemFood("GoldenApple", "minecraft/textures/item/golden_apple.png", 4f, 1.2f));
            context.Register(new ItemShears());

            // Swords per Minecraft material (attack damage): wood 4, stone 5, iron 6, gold 4, diamond 7.
            RegisterSword(context, "Wooden", "wooden", 4f, 0);
            RegisterSword(context, "Stone", "stone", 5f, 1);
            RegisterSword(context, "Iron", "iron", 6f, 2);
            RegisterSword(context, "Golden", "golden", 4f, 0);
            RegisterSword(context, "Diamond", "diamond", 7f, 3);

            // Armor per Minecraft material (defense points: helmet, chestplate, leggings, boots).
            RegisterArmorSet(context, "Leather", "leather", 1, 3, 2, 1);
            RegisterArmorSet(context, "Chainmail", "chainmail", 2, 5, 4, 1);
            RegisterArmorSet(context, "Golden", "golden", 2, 5, 3, 1);
            RegisterArmorSet(context, "Iron", "iron", 2, 6, 5, 2);
            RegisterArmorSet(context, "Diamond", "diamond", 3, 8, 6, 3);

            // Mining tools per Minecraft material (speed multiplier, harvest tier): wood 2/0, stone 4/1,
            // iron 6/2, gold 12/0, diamond 8/3. Each material gets a pickaxe, axe, and shovel.
            RegisterToolSet(context, "Wooden", "wooden", 2f, 0);
            RegisterToolSet(context, "Stone", "stone", 4f, 1);
            RegisterToolSet(context, "Iron", "iron", 6f, 2);
            RegisterToolSet(context, "Golden", "golden", 12f, 0);
            RegisterToolSet(context, "Diamond", "diamond", 8f, 3);

            context.Register<BlockDataMetadata>();
            context.Register<BlockDataFurnace>();
            context.Register<BlockDataChest>();

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

        private static void RegisterSword(PluginContext context, string prefix, string material, float damage, int tier)
            => context.Register(new ItemSword(prefix + "Sword", $"minecraft/textures/item/{material}_sword.png", damage, tier));

        private static void RegisterArmorSet(PluginContext context, string prefix, string material,
            int helmet, int chestplate, int leggings, int boots)
        {
            context.Register(new ItemArmor(prefix + "Helmet", $"minecraft/textures/item/{material}_helmet.png", ArmorSlot.Helmet, helmet));
            context.Register(new ItemArmor(prefix + "Chestplate", $"minecraft/textures/item/{material}_chestplate.png", ArmorSlot.Chestplate, chestplate));
            context.Register(new ItemArmor(prefix + "Leggings", $"minecraft/textures/item/{material}_leggings.png", ArmorSlot.Leggings, leggings));
            context.Register(new ItemArmor(prefix + "Boots", $"minecraft/textures/item/{material}_boots.png", ArmorSlot.Boots, boots));
        }

        // A wood species' log/planks/leaves. `id` is the Minecraft resource prefix (e.g. "dark_oak"); `Name`
        // is its PascalCase registry name. Hardness/tool match Minecraft.
        private static void RegisterWoodSet(PluginContext context, string id)
        {
            var name = ToPascal(id);
            context.Register(new BlockBasic(name + "Log", "minecraft:block/" + id + "_log", true, 2.0f, ToolType.Axe) { CreativeTab = CreativeTab.NaturalBlocks });
            context.Register(new BlockBasic(name + "Planks", "minecraft:block/" + id + "_planks", true, 2.0f, ToolType.Axe));
            context.Register(new BlockLeaves(name + "Leaves", "minecraft:block/" + id + "_leaves"));
        }

        private static void RegisterStone(PluginContext context, string name, string id, CreativeTab tab = CreativeTab.BuildingBlocks)
            => context.Register(new BlockBasic(name, "minecraft:block/" + id, true, 1.5f, ToolType.Pickaxe, 0, true) { CreativeTab = tab });

        /// <summary>"dark_oak" → "DarkOak": maps a Minecraft snake_case id to a registry-name segment.</summary>
        private static string ToPascal(string id)
        {
            var parts = id.Split('_');
            for (var i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0)
                    parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            return string.Concat(parts);
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
                "minecraft:entity/pig/pig_temperate", "Vanilla/Models/Entity/pig.geo.json",
                loot: new LootTable(new LootDrop("Vanilla:Porkchop", 1, 3)));
            var cow = new EntityType("Cow", EntityKind.Creature, 0.9f, 1.4f, 10f, 0.1f, false,
                "minecraft:entity/cow/cow_temperate", "Vanilla/Models/Entity/cow.geo.json",
                loot: new LootTable(new LootDrop("Vanilla:Beef", 1, 3), new LootDrop("Vanilla:Leather", 0, 2)));
            // The sheep carries a wool overlay (its own texture) that its SheepData hides once sheared.
            var sheep = new EntityType("Sheep", EntityKind.Creature, 0.9f, 1.3f, 8f, 0.1f, false,
                "minecraft:entity/sheep/sheep", "Vanilla/Models/Entity/sheep.geo.json",
                "Vanilla/Models/Entity/sheep_wool.geo.json", "minecraft:entity/sheep/sheep_wool",
                () => new SheepData(),
                loot: new LootTable(new LootDrop("Vanilla:Mutton", 1, 2)));
            var chicken = new EntityType("Chicken", EntityKind.Creature, 0.4f, 0.7f, 4f, 0.08f, false,
                "minecraft:entity/chicken/chicken_temperate", "Vanilla/Models/Entity/chicken.geo.json",
                loot: new LootTable(new LootDrop("Vanilla:Chicken", 1, 1), new LootDrop("Vanilla:Feather", 0, 2)));
            var zombie = new EntityType("Zombie", EntityKind.Creature, 0.6f, 1.95f, 20f, 0.13f, true,
                "minecraft:entity/zombie/zombie", "System/Models/Entity/biped.geo.json",
                attackDamage: 3f, loot: new LootTable(new LootDrop("Vanilla:RottenFlesh", 0, 2)));
            // The Enderman: a tall, fast neutral mob with the long-limbed humanoid silhouette. Its slender
            // geometry (2x30 limbs, 8x12x4 body, 8x8x8 head) maps the official 64x32 enderman sheet directly.
            // Neutral until provoked: it wanders peacefully until a player looks straight at it, then gives chase;
            // killing it drops an ender pearl, as in Minecraft.
            var enderman = new EntityType("Enderman", EntityKind.Creature, 0.6f, 2.9f, 40f, 0.17f, false,
                "minecraft:entity/enderman/enderman", "Vanilla/Models/Entity/enderman.geo.json",
                neutralUntilProvoked: true,
                loot: new LootTable(new LootDrop("Vanilla:EnderPearl", 0, 1)));

            context.Register(pig);
            context.Register(cow);
            context.Register(sheep);
            context.Register(chicken);
            context.Register(zombie);
            context.Register(enderman);
            context.Register(new EntityType("Item", EntityKind.Item, 0.25f, 0.25f, 1f, 0f, false, null, null));
            context.Register(new EntityType("FallingBlock", EntityKind.FallingBlock,
                EntityFallingBlock.Size, EntityFallingBlock.Size, 1f, 0f, false, null, null));

            // The thrown ender pearl: a small projectile rendered from the pearl item sprite. The ItemEnderPearl
            // throws it; on impact it teleports the thrower (see EntityProjectile / PlayerTeleportPacket).
            var enderPearlProjectile = new EntityType("EnderPearlProjectile", EntityKind.Projectile,
                0.25f, 0.25f, 1f, 0f, false, "minecraft:item/ender_pearl", null);
            context.Register(enderPearlProjectile);
            context.Register(new ItemEnderPearl(enderPearlProjectile));

            context.Register(new ItemSpawnEgg(pig, "minecraft/textures/item/pig_spawn_egg.png"));
            context.Register(new ItemSpawnEgg(cow, "minecraft/textures/item/cow_spawn_egg.png"));
            context.Register(new ItemSpawnEgg(sheep, "minecraft/textures/item/sheep_spawn_egg.png"));
            context.Register(new ItemSpawnEgg(chicken, "minecraft/textures/item/chicken_spawn_egg.png"));
            context.Register(new ItemSpawnEgg(zombie, "minecraft/textures/item/zombie_spawn_egg.png"));
            context.Register(new ItemSpawnEgg(enderman, "minecraft/textures/item/enderman_spawn_egg.png"));
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

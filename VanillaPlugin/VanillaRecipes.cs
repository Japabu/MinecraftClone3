using MinecraftClone3API.Items;
using MinecraftClone3API.Plugins;

namespace VanillaPlugin
{
    /// <summary>Registers the vanilla crafting recipes. Must run after the blocks and items they reference are
    /// registered (the factory resolves ingredient/result keys to item ids immediately).</summary>
    internal static class VanillaRecipes
    {
        public static void Register(PluginContext context)
        {
            context.Register(Recipes.Shapeless("recipe.oak_planks", "Vanilla:OakPlanks", 4, "Vanilla:OakLog"));
            context.Register(Recipes.Shaped("recipe.stick", "Vanilla:Stick", 4,
                new[] {"P", "P"}, ('P', "Vanilla:OakPlanks")));
            context.Register(Recipes.Shaped("recipe.crafting_table", "Vanilla:CraftingTable", 1,
                new[] {"PP", "PP"}, ('P', "Vanilla:OakPlanks")));
            context.Register(Recipes.Shaped("recipe.oak_stairs", "Vanilla:OakStairs", 4,
                new[] {"P  ", "PP ", "PPP"}, ('P', "Vanilla:OakPlanks")));
            context.Register(Recipes.Shaped("recipe.torch", "Vanilla:Torch", 4,
                new[] {"C", "S"}, ('C', "Vanilla:Coal"), ('S', "Vanilla:Stick")));
            context.Register(Recipes.Shaped("recipe.stone_bricks", "Vanilla:StoneBricks", 4,
                new[] {"SS", "SS"}, ('S', "Vanilla:Stone")));
        }
    }
}

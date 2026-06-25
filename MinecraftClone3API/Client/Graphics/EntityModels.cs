using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Box-model factories for the built-in entities, authored to the official Minecraft models (each box uses
    /// Mojang's exact pivots, offsets, and dimensions so the entity sheets map straight on). Minecraft authors
    /// its models Y-down with feet at y=24 and the head facing −Z; these are converted to our convention (Y-up,
    /// feet at y=0, facing +Z) by <c>ours_y = 24 − mc_y</c> and <c>ours_z = −mc_z</c>, which leaves box sizes —
    /// and therefore the texture unwrap — unchanged. Quadruped bodies carry a baked 90° pitch so the long box
    /// (whose texture unwraps upright) lies horizontal. Part names drive animation (see <see cref="EntityRenderer"/>).
    /// </summary>
    public static class EntityModels
    {
        private const float Deg90 = MathHelper.PiOver2;

        private static Vector3 P(float x, float y, float z) => new Vector3(x, y, z) / 16f;

        /// <summary>Humanoid biped, used for both remote players (steve skin) and zombies (64×64 sheet).</summary>
        public static EntityModel Biped()
        {
            var m = new EntityModel(64, 64);

            m.AddPart(new ModelPart("head", P(0, 24, 0))).AddBox(P(-4, 0, -4), P(4, 8, 4), 0, 0);
            m.AddPart(new ModelPart("body", P(0, 12, 0))).AddBox(P(-4, 0, -2), P(4, 12, 2), 16, 16);
            m.AddPart(new ModelPart("arm0", P(-6, 22, 0))).AddBox(P(-2, -10, -2), P(2, 2, 2), 40, 16);
            m.AddPart(new ModelPart("arm1", P(6, 22, 0))).AddBox(P(-2, -10, -2), P(2, 2, 2), 32, 48);
            m.AddPart(new ModelPart("leg0", P(-2, 12, 0))).AddBox(P(-2, -12, -2), P(2, 0, 2), 0, 16);
            m.AddPart(new ModelPart("leg1", P(2, 12, 0))).AddBox(P(-2, -12, -2), P(2, 0, 2), 16, 48);

            return m;
        }

        /// <summary>Pig (64×32 sheet).</summary>
        public static EntityModel Pig()
        {
            var m = new EntityModel(64, 32);

            m.AddPart(new ModelPart("head", P(0, 12, 6))).AddBox(P(-4, -4, 0), P(4, 4, 8), 0, 0)
                .AddBox(P(-2, -3, 8), P(2, 0, 9), 16, 16); // snout
            m.AddPart(new ModelPart("body", P(0, 13, -2), new Vector3(Deg90, 0, 0)))
                .AddBox(P(-5, -6, -1), P(5, 10, 7), 28, 8);
            Legs(m, 6, 3, 6, 0, 16);

            return m;
        }

        /// <summary>Cow (64×32 sheet).</summary>
        public static EntityModel Cow()
        {
            var m = new EntityModel(64, 32);

            m.AddPart(new ModelPart("head", P(0, 20, 8))).AddBox(P(-4, -4, 0), P(4, 4, 6), 0, 0)
                .AddBox(P(-5, 2, 3), P(-4, 5, 4), 22, 0)   // right horn
                .AddBox(P(4, 2, 3), P(5, 5, 4), 22, 0);    // left horn
            m.AddPart(new ModelPart("body", P(0, 19, -2), new Vector3(Deg90, 0, 0)))
                .AddBox(P(-6, -8, -3), P(6, 10, 7), 18, 4)
                .AddBox(P(-2, -8, 7), P(2, -2, 8), 52, 0); // udder
            Legs(m, 12, 4, 7, 0, 16);

            return m;
        }

        /// <summary>Sheep (64×32 sheet — the body/wool texture).</summary>
        public static EntityModel Sheep()
        {
            var m = new EntityModel(64, 32);

            m.AddPart(new ModelPart("head", P(0, 18, 8))).AddBox(P(-3, -4, 0), P(3, 4, 6), 0, 0);
            m.AddPart(new ModelPart("body", P(0, 19, -2), new Vector3(Deg90, 0, 0)))
                .AddBox(P(-4, -6, 1), P(4, 10, 7), 28, 8);
            Legs(m, 12, 3, 6, 0, 16);

            return m;
        }

        /// <summary>Chicken (64×32 sheet).</summary>
        public static EntityModel Chicken()
        {
            var m = new EntityModel(64, 32);

            m.AddPart(new ModelPart("head", P(0, 9, 4)))
                .AddBox(P(-2, 0, -1), P(2, 6, 2), 0, 0)
                .AddBox(P(-2, 2, 2), P(2, 4, 4), 14, 0)   // beak
                .AddBox(P(-1, 0, 1), P(1, 2, 3), 14, 4);  // wattle
            m.AddPart(new ModelPart("body", P(0, 8, 0), new Vector3(Deg90, 0, 0)))
                .AddBox(P(-3, -4, -3), P(3, 4, 3), 0, 9);

            m.AddPart(new ModelPart("wing0", P(-4, 11, 0))).AddBox(P(0, -4, -3), P(1, 0, 3), 24, 13);
            m.AddPart(new ModelPart("wing1", P(4, 11, 0))).AddBox(P(-1, -4, -3), P(0, 0, 3), 24, 13);

            m.AddPart(new ModelPart("leg0", P(-2, 5, -1))).AddBox(P(-1, -5, 0), P(2, 0, 3), 26, 0);
            m.AddPart(new ModelPart("leg1", P(1, 5, -1))).AddBox(P(-1, -5, 0), P(2, 0, 3), 26, 0);

            return m;
        }

        // Four legs of a quadruped: front-right/front-left/back-right/back-left, named leg0..leg3 so the renderer
        // animates them on diagonals. Pivots at the leg top so they swing from the hip.
        private static void Legs(EntityModel m, int lengthPx, float halfX, float halfZ, int u, int v)
        {
            var from = P(-2, -lengthPx, -2);
            var to = P(2, 0, 2);
            m.AddPart(new ModelPart("leg0", P(-halfX, lengthPx, halfZ))).AddBox(from, to, u, v);
            m.AddPart(new ModelPart("leg1", P(halfX, lengthPx, halfZ))).AddBox(from, to, u, v);
            m.AddPart(new ModelPart("leg2", P(-halfX, lengthPx, -halfZ))).AddBox(from, to, u, v);
            m.AddPart(new ModelPart("leg3", P(halfX, lengthPx, -halfZ))).AddBox(from, to, u, v);
        }
    }
}

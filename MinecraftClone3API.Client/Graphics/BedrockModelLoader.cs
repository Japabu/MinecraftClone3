using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Parses a Bedrock-edition geometry JSON file (the Blockbench-native mob format) into a GL-free
    /// <see cref="EntityModel"/>. Bedrock authors y-up with the feet at y=0 and the model facing −Z, while our
    /// convention faces +Z, so every position is reflected through z (<c>z → −z</c>). A Bedrock cube
    /// <c>origin</c> is an absolute model-space corner; our renderer rotates each box about its part pivot, so
    /// the cube is rebased relative to the (reflected) bone pivot. Bone names drive animation, so author them as
    /// <c>head</c>/<c>body</c>/<c>leg0..3</c>/<c>arm0..1</c>/<c>wing0..1</c> (see <see cref="EntityRenderer"/>).
    /// Per-cube <c>uv</c>, <c>inflate</c>, and X-axis bone <c>rotation</c> (the quadruped body pitch) are honored;
    /// bone <c>parent</c> hierarchies, cube <c>mirror</c>, and Y/Z rotation sign conventions are not interpreted.
    /// </summary>
    public static class BedrockModelLoader
    {
        public static EntityModel Parse(string json)
        {
            var geometry = (JArray) JObject.Parse(json)["minecraft:geometry"];
            var description = (JObject) geometry[0]["description"];
            var model = new EntityModel(
                description["texture_width"].Value<int>(),
                description["texture_height"].Value<int>());

            foreach (JObject bone in (JArray) geometry[0]["bones"])
            {
                var pivot = ReadVector(bone["pivot"]);
                var ourPivot = new Vector3(pivot.X, pivot.Y, -pivot.Z) / 16f;
                var rotation = bone["rotation"] != null ? ReadVector(bone["rotation"]) : Vector3.Zero;
                var part = new ModelPart(bone["name"].Value<string>(), ourPivot, new Vector3(
                    MathHelper.DegreesToRadians(rotation.X),
                    MathHelper.DegreesToRadians(rotation.Y),
                    MathHelper.DegreesToRadians(rotation.Z)));
                model.AddPart(part);

                if (bone["cubes"] == null) continue;
                foreach (JObject cube in (JArray) bone["cubes"])
                {
                    var origin = ReadVector(cube["origin"]);
                    var size = ReadVector(cube["size"]);
                    var inflate = cube["inflate"]?.Value<float>() ?? 0f;

                    // Absolute corners in Bedrock space; reflecting z swaps which face is the minimum corner.
                    // Inflate stays separate so the UV unwrap keeps the base size (see ModelBox.Inflate).
                    var min = new Vector3(origin.X, origin.Y, -(origin.Z + size.Z));
                    var max = new Vector3(origin.X + size.X, origin.Y + size.Y, -origin.Z);

                    part.AddBox(min / 16f - ourPivot, max / 16f - ourPivot,
                        cube["uv"][0].Value<int>(), cube["uv"][1].Value<int>(), inflate / 16f);
                }
            }

            return model;
        }

        private static Vector3 ReadVector(JToken token) =>
            new Vector3(token[0].Value<float>(), token[1].Value<float>(), token[2].Value<float>());
    }
}

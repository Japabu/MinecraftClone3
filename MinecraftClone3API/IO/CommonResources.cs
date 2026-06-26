using MinecraftClone3API.Graphics;
using Newtonsoft.Json;

namespace MinecraftClone3API.IO
{
    public static class CommonResources
    {
        /// <summary>Fallback render assets used when a model/texture is missing. Plain CPU data (a parsed
        /// <see cref="BlockModel"/> and a <see cref="BlockTexture"/> index pair), so they live in Core and
        /// are referenced by <c>Block</c>/<c>BlockModel</c> at construction. Populated by the client at load
        /// (<c>ClientResources.Load</c>); null on the headless server, which never meshes.</summary>
        public static BlockModel MissingModel;
        public static BlockTexture MissingTexture;

        public static void Load()
        {
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings {Converters = {new CustomJsonConverter()}};
        }
    }
}

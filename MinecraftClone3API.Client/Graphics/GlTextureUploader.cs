using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    /// <summary>Client-only GL side of the block texture atlas: uploads the CPU texture data accumulated by
    /// <see cref="BlockTextureManager"/> into per-size <see cref="TextureArray"/>s and binds them. Kept out
    /// of Core so the headless server never links GL.</summary>
    public static class GlTextureUploader
    {
        private static readonly TextureArray[] TextureArrays = new TextureArray[BlockTextureManager.Sizes.Length];

        public static void Bind()
        {
            for (var i = 0; i < TextureArrays.Length; i++)
                TextureArrays[i].Bind(TextureUnit.Texture0 + i);
        }

        public static void Upload()
        {
            var sizes = BlockTextureManager.Sizes;
            for (var i = 0; i < sizes.Length; i++)
            {
                var datas = BlockTextureManager.DatasFor(i);
                TextureArrays[i]?.Dispose();
                TextureArrays[i] = new TextureArray(sizes[i], sizes[i], datas.Count);
                for (var j = 0; j < datas.Count; j++)
                    TextureArrays[i].SetTexture(j, datas[j]);
                TextureArrays[i].GenerateMipmaps();
            }
        }
    }
}

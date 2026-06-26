using System;
using OpenTK.Graphics.OpenGL4;

namespace MinecraftClone3API.Graphics
{
    public class TextureArray
    {
        public readonly int Width;
        public readonly int Height;

        private readonly int _id;

        public TextureArray(int width, int height, int count)
        {
            Width = width;
            Height = height;

            _id = GL.GenTexture();
            Bind(TextureUnit.Texture0);
            // Mutable TexImage3D (GL 4.1) instead of TexStorage3D (GL 4.2); GenerateMipmaps()
            // builds the mip chain afterwards. macOS caps OpenGL at 4.1.
            GL.TexImage3D(TextureTarget.Texture2DArray, 0, PixelInternalFormat.Rgba8, width, height, count, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        }

        public void SetTexture(int index, TextureData data)
        {
            GL.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, index, data.Width, data.Height, 1, PixelFormat.Rgba,
                PixelType.UnsignedByte, data.Pixels);
            data.Dispose();
        }

        public void GenerateMipmaps()
        {
            Bind(TextureUnit.Texture0);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2DArray);
        }

        public void Bind(TextureUnit textureUnit)
        {
            GL.ActiveTexture(textureUnit);
            GL.BindTexture(TextureTarget.Texture2DArray, _id);
        }

        public void Dispose() => GL.DeleteTexture(_id);
    }
}
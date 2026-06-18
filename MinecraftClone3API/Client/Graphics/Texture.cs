using OpenTK.Graphics.OpenGL4;
using GLPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace MinecraftClone3API.Graphics
{
    public class Texture
    {
        public static Texture FromId(int id, int width, int height) => new Texture(id, width, height);

        public readonly int Id;
        public readonly int Width;
        public readonly int Height;

        private Texture(int id, int width, int height)
        {
            Id = id;
            Width = width;
            Height = height;
        }

        public Texture(TextureData data)
        {
            Id = GL.GenTexture();
            Width = data.Width;
            Height = data.Height;

            Bind(TextureUnit.Texture0);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, Width, Height, 0, GLPixelFormat.Rgba,
                PixelType.UnsignedByte, data.Pixels);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

            data.Dispose();
        }

        public void Bind(TextureUnit textureUnit)
        {
            GL.ActiveTexture(textureUnit);
            GL.BindTexture(TextureTarget.Texture2D, Id);
        }
    }
}
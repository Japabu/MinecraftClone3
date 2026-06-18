using System;

namespace MinecraftClone3API.Graphics
{
    public class TextureData : IDisposable
    {
        // RGBA8 pixel data, top-left origin (matches the legacy System.Drawing behaviour).
        public readonly byte[] Pixels;
        public readonly int Width;
        public readonly int Height;

        public TextureData(byte[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }

        public void Dispose()
        {
            // Pixel data is a managed array; nothing unmanaged to release.
        }
    }
}

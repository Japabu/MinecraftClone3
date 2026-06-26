using System;
using System.Collections.Generic;

namespace MinecraftClone3API.Graphics
{
    /// <summary>One animated texture: its frames (each a normal <see cref="BlockTexture"/> layer, in
    /// order) and the tick count each frame is shown for. Block faces currently bake frame 0; this record
    /// retains every frame + the timing so a future animator can cycle them without re-slicing.</summary>
    public readonly struct AnimatedTexture
    {
        public readonly BlockTexture[] Frames;
        public readonly int FrameTime;

        public AnimatedTexture(BlockTexture[] frames, int frameTime)
        {
            Frames = frames;
            FrameTime = frameTime;
        }
    }

    public static class BlockTextureManager
    {
        public static readonly int[] Sizes = {16, 64, 256, 1024};
        private static readonly List<TextureData>[] TextureDatas = new List<TextureData>[Sizes.Length];

        private static readonly List<AnimatedTexture> Animated = new List<AnimatedTexture>();
        public static IReadOnlyList<AnimatedTexture> AnimatedTextures => Animated;

        static BlockTextureManager()
        {
            for (var i = 0; i < TextureDatas.Length; i++)
                TextureDatas[i] = new List<TextureData>();
        }

        /// <summary>The accumulated CPU texture data for size bucket <paramref name="sizeIndex"/>, in upload
        /// order. The GL uploader (<c>GlTextureUploader</c>, client-only) reads this to fill the texture
        /// arrays; Core itself never touches GL.</summary>
        public static IReadOnlyList<TextureData> DatasFor(int sizeIndex) => TextureDatas[sizeIndex];

        internal static BlockTexture LoadTexture(TextureData data)
        {
            var size = Math.Max(data.Width, data.Height);

            for (var i = 0; i < Sizes.Length; i++)
            {
                if (size > Sizes[i]) continue;

                var texture = new BlockTexture(i, TextureDatas[i].Count);
                TextureDatas[i].Add(data);
                return texture;
            }

            throw new Exception("Texture is too big!");
        }

        /// <summary>Slices a vertical animation strip (<paramref name="frameCount"/> square frames stacked
        /// top-to-bottom) into individual square frame textures, uploads them all, and returns frame 0.
        /// Frame 0 is what block faces sample today; every frame plus <paramref name="frameTime"/> is kept
        /// in <see cref="AnimatedTextures"/> so animation can be added later with no re-slice.</summary>
        internal static BlockTexture LoadAnimatedTexture(TextureData strip, int frameCount, int frameTime)
        {
            var frameSize = strip.Width;
            var bytesPerFrame = frameSize * frameSize * 4;
            var frames = new BlockTexture[frameCount];

            for (var f = 0; f < frameCount; f++)
            {
                var pixels = new byte[bytesPerFrame];
                Array.Copy(strip.Pixels, f * bytesPerFrame, pixels, 0, bytesPerFrame);
                frames[f] = LoadTexture(new TextureData(pixels, frameSize, frameSize));
            }

            Animated.Add(new AnimatedTexture(frames, frameTime));
            return frames[0];
        }
    }
}

using System.Collections.Generic;
using System.Diagnostics;
using MinecraftClone3API.Graphics.Rhi;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Cycles Minecraft animated block textures (water, lava, fire, the nether portal, …) by rewriting the
    /// frame the block faces sample. Every animation strip is sliced into per-frame layers at load
    /// (<see cref="BlockTextureManager.AnimatedTextures"/>) and meshes bake frame 0's layer, so advancing a
    /// frame is just a mip-0 write of that frame's pixels into frame 0's layer — no shader, mesher, or
    /// bind-group change. Runs on the main render thread, driven once per frame from the world conductor.
    /// </summary>
    public static class BlockAnimator
    {
        private const double SecondsPerTick = 1.0 / 20.0;

        private sealed class Anim
        {
            public GpuTexture Array;
            public uint Layer;
            public uint Size;
            public byte[][] Frames;
            public int FrameTime;
            public int Current;
            public double Accumulator;
        }

        private static readonly List<Anim> Anims = new List<Anim>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double _last;

        /// <summary>Snapshots the animated textures into a flat play list. Call once, right after
        /// <see cref="BlockTextureUploader.Upload"/> has built the atlas arrays.</summary>
        public static void Init()
        {
            Anims.Clear();
            _last = Clock.Elapsed.TotalSeconds;

            foreach (var anim in BlockTextureManager.AnimatedTextures)
            {
                if (anim.Frames == null || anim.Frames.Length < 2) continue;

                var first = anim.Frames[0];
                var datas = BlockTextureManager.DatasFor(first.ArrayId);
                var frames = new byte[anim.Frames.Length][];
                for (var f = 0; f < anim.Frames.Length; f++)
                    frames[f] = datas[anim.Frames[f].TextureId].Pixels;

                Anims.Add(new Anim
                {
                    Array = BlockTextureUploader.ArrayAt(first.ArrayId),
                    Layer = (uint)first.TextureId,
                    Size = (uint)datas[first.TextureId].Width,
                    Frames = frames,
                    FrameTime = anim.FrameTime,
                });
            }
        }

        /// <summary>Advances every animation by real elapsed time and re-uploads any frame that changed. The
        /// queue write is recorded before the geometry pass submits, so the new frame is visible the same
        /// frame. Cheap: a write happens only when a frame actually flips, not every render frame.</summary>
        public static void Update()
        {
            if (Anims.Count == 0) return;

            var now = Clock.Elapsed.TotalSeconds;
            var dt = now - _last;
            _last = now;
            if (dt <= 0) return;
            if (dt > 0.25) dt = 0.25;

            foreach (var anim in Anims)
            {
                anim.Accumulator += dt;
                var step = anim.FrameTime * SecondsPerTick;
                if (anim.Accumulator < step) continue;

                var advance = (int)(anim.Accumulator / step);
                anim.Accumulator -= advance * step;
                var next = (anim.Current + advance) % anim.Frames.Length;
                if (next == anim.Current) continue;

                anim.Current = next;
                anim.Array.Upload<byte>(anim.Frames[next], 0, anim.Layer, anim.Size, anim.Size, 4);
            }
        }
    }
}

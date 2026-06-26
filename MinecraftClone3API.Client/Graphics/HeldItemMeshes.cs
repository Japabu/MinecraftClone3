using System.Collections.Generic;
using MinecraftClone3API.IO;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Builds the 3D held-item mesh for a flat (non-block) item by extruding its 2D sprite — the front + back
    /// faces plus a one-pixel-thick side wall along every opaque/transparent edge, exactly like Minecraft's
    /// "generated" item models. The sprite is sampled through the shared block-texture arrays (the entity shader
    /// samples those), so every flat item's texture must be registered into the arrays at load
    /// (<see cref="RegisterTextures"/>, before the GPU upload). Meshes are built lazily and cached; the item is
    /// centred on x/y in [-0.5,0.5] with a thin z so the viewmodel can pose it like a block.
    /// </summary>
    internal static class HeldItemMeshes
    {
        // Half the item's z thickness (≈1 sprite pixel), in the centred [-0.5,0.5] item space.
        private const float HalfThickness = 0.5f / 16f;
        private const byte OpaqueCutoff = 128;

        private static readonly Dictionary<ushort, VertexArrayObject> Cache = new Dictionary<ushort, VertexArrayObject>();

        /// <summary>Registers every flat item's sprite into the block-texture arrays so the entity shader can
        /// sample it. Must run before the texture-array upload (alongside the entity/block-entity model load).</summary>
        public static void RegisterTextures()
        {
            foreach (var item in GameRegistry.Items)
            {
                if (item.GetBlock() != null || item.TexturePath == null || !ResourceReader.Exists(item.TexturePath)) continue;
                ResourceReader.ReadBlockTexture(item.TexturePath);
            }
        }

        /// <summary>The extruded mesh for a flat item (built + cached on first use), or null if it has no sprite.</summary>
        internal static VertexArrayObject Get(Item item)
        {
            if (Cache.TryGetValue(item.Id, out var mesh)) return mesh;
            mesh = Build(item);
            Cache[item.Id] = mesh;
            return mesh;
        }

        private static VertexArrayObject Build(Item item)
        {
            if (item.TexturePath == null || !ResourceReader.Exists(item.TexturePath)) return null;

            var tex = ResourceReader.ReadBlockTexture(item.TexturePath);
            var data = ResourceReader.ReadTextureData(item.TexturePath);
            var layer = BlockTextureManager.Sizes[tex.ArrayId];
            var w = data.Width;
            var h = data.Height;
            if (w <= 0 || h <= 0) return null;

            var vao = new VertexArrayObject();
            const float t = HalfThickness;

            // Front (+Z) and back (−Z): the whole sprite; transparent texels are discarded by the shader.
            EntityRenderer.Quad(vao, tex, layer, new Vector3(0, 0, 1),
                new Vector3(-0.5f, 0.5f, t), new Vector3(0.5f, 0.5f, t),
                new Vector3(-0.5f, -0.5f, t), new Vector3(0.5f, -0.5f, t), 0, 0, w, h);
            EntityRenderer.Quad(vao, tex, layer, new Vector3(0, 0, -1),
                new Vector3(-0.5f, 0.5f, -t), new Vector3(0.5f, 0.5f, -t),
                new Vector3(-0.5f, -0.5f, -t), new Vector3(0.5f, -0.5f, -t), 0, 0, w, h);

            // Side walls along every opaque pixel that borders a transparent one (or the sprite edge).
            for (var py = 0; py < h; py++)
            for (var px = 0; px < w; px++)
            {
                if (!Opaque(data, px, py)) continue;
                var xL = (float) px / w - 0.5f;
                var xR = (float) (px + 1) / w - 0.5f;
                var yT = 0.5f - (float) py / h;
                var yB = 0.5f - (float) (py + 1) / h;

                if (!Opaque(data, px + 1, py))
                    EntityRenderer.Quad(vao, tex, layer, new Vector3(1, 0, 0),
                        new Vector3(xR, yT, t), new Vector3(xR, yT, -t),
                        new Vector3(xR, yB, t), new Vector3(xR, yB, -t), px, py, px + 1, py + 1);
                if (!Opaque(data, px - 1, py))
                    EntityRenderer.Quad(vao, tex, layer, new Vector3(-1, 0, 0),
                        new Vector3(xL, yT, -t), new Vector3(xL, yT, t),
                        new Vector3(xL, yB, -t), new Vector3(xL, yB, t), px, py, px + 1, py + 1);
                if (!Opaque(data, px, py - 1))
                    EntityRenderer.Quad(vao, tex, layer, new Vector3(0, 1, 0),
                        new Vector3(xL, yT, -t), new Vector3(xR, yT, -t),
                        new Vector3(xL, yT, t), new Vector3(xR, yT, t), px, py, px + 1, py + 1);
                if (!Opaque(data, px, py + 1))
                    EntityRenderer.Quad(vao, tex, layer, new Vector3(0, -1, 0),
                        new Vector3(xL, yB, t), new Vector3(xR, yB, t),
                        new Vector3(xL, yB, -t), new Vector3(xR, yB, -t), px, py, px + 1, py + 1);
            }

            vao.Upload();
            return vao;
        }

        private static bool Opaque(TextureData data, int x, int y)
        {
            if (x < 0 || y < 0 || x >= data.Width || y >= data.Height) return false;
            return data.Pixels[(y * data.Width + x) * 4 + 3] >= OpaqueCutoff;
        }
    }
}

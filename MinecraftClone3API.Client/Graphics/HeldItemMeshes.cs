using System.Collections.Generic;
using MinecraftClone3API.Entities;
using MinecraftClone3API.IO;
using MinecraftClone3API.Items;
using MinecraftClone3API.Util;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// Builds the 3D mesh for a flat (non-block) item sprite by extruding its 2D texture — the front + back faces
    /// plus a one-pixel-thick side wall along every opaque/transparent edge, exactly like Minecraft's "generated"
    /// item models. The sprite is sampled through the shared block-texture arrays (the entity shader samples
    /// those), so every flat sprite must be registered into the arrays at load (<see cref="RegisterTextures"/>,
    /// before the GPU upload). Meshes are built lazily and cached by texture path, centred on x/y in [-0.5,0.5]
    /// with a thin z so the held-item viewmodel and thrown-projectile billboard can pose it like a block.
    /// </summary>
    internal static class HeldItemMeshes
    {
        // Half the item's z thickness (≈1 sprite pixel), in the centred [-0.5,0.5] item space.
        private const float HalfThickness = 0.5f / 16f;
        private const byte OpaqueCutoff = 128;

        private static readonly Dictionary<string, EntityRenderer.PartMesh> Cache =
            new Dictionary<string, EntityRenderer.PartMesh>();

        /// <summary>Registers every flat item's sprite — and every flat-sprite projectile's texture — into the
        /// block-texture arrays so the entity shader can sample it. Must run before the texture-array upload
        /// (alongside the entity/block-entity model load).</summary>
        public static void RegisterTextures()
        {
            foreach (var item in GameRegistry.Items)
            {
                if (item.GetBlock() != null || item.TexturePath == null || !ResourceReader.Exists(item.TexturePath)) continue;
                ResourceReader.ReadBlockTexture(item.TexturePath);
            }

            // Projectile sprites are "minecraft:item/…" locations that ReadBlockTexture resolves (Exists does
            // not), so register them directly into the arrays — same as the entity sheets.
            foreach (var type in GameRegistry.EntityTypes)
            {
                if (type.Kind != EntityKind.Projectile || type.ModelPath != null || type.TexturePath == null) continue;
                ResourceReader.ReadBlockTexture(type.TexturePath);
            }
        }

        /// <summary>The extruded mesh for a flat item (built + cached on first use), or null if it has no sprite.</summary>
        internal static EntityRenderer.PartMesh Get(Item item)
            => item.TexturePath == null ? null : GetByTexture(item.TexturePath);

        /// <summary>The extruded mesh for a sprite at <paramref name="texturePath"/>, shared by held flat items and
        /// thrown projectiles that draw from the same texture.</summary>
        internal static EntityRenderer.PartMesh GetByTexture(string texturePath)
        {
            if (texturePath == null) return null;
            if (Cache.TryGetValue(texturePath, out var mesh)) return mesh;
            mesh = Build(texturePath);
            Cache[texturePath] = mesh;
            return mesh;
        }

        private static EntityRenderer.PartMesh Build(string texturePath)
        {
            if (!ResourceReader.Exists(texturePath)) return null;

            var tex = ResourceReader.ReadBlockTexture(texturePath);
            var data = ResourceReader.ReadTextureData(texturePath);
            var layer = BlockTextureManager.Sizes[tex.ArrayId];
            var w = data.Width;
            var h = data.Height;
            if (w <= 0 || h <= 0) return null;

            var mesh = new MeshBuffer();
            const float t = HalfThickness;

            // Front (+Z) and back (−Z): the whole sprite; transparent texels are discarded by the shader.
            EntityRenderer.Quad(mesh, tex, layer, new Vector3(0, 0, 1),
                new Vector3(-0.5f, 0.5f, t), new Vector3(0.5f, 0.5f, t),
                new Vector3(-0.5f, -0.5f, t), new Vector3(0.5f, -0.5f, t), 0, 0, w, h);
            EntityRenderer.Quad(mesh, tex, layer, new Vector3(0, 0, -1),
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
                    EntityRenderer.Quad(mesh, tex, layer, new Vector3(1, 0, 0),
                        new Vector3(xR, yT, t), new Vector3(xR, yT, -t),
                        new Vector3(xR, yB, t), new Vector3(xR, yB, -t), px, py, px + 1, py + 1);
                if (!Opaque(data, px - 1, py))
                    EntityRenderer.Quad(mesh, tex, layer, new Vector3(-1, 0, 0),
                        new Vector3(xL, yT, -t), new Vector3(xL, yT, t),
                        new Vector3(xL, yB, -t), new Vector3(xL, yB, t), px, py, px + 1, py + 1);
                if (!Opaque(data, px, py - 1))
                    EntityRenderer.Quad(mesh, tex, layer, new Vector3(0, 1, 0),
                        new Vector3(xL, yT, -t), new Vector3(xR, yT, -t),
                        new Vector3(xL, yT, t), new Vector3(xR, yT, t), px, py, px + 1, py + 1);
                if (!Opaque(data, px, py + 1))
                    EntityRenderer.Quad(mesh, tex, layer, new Vector3(0, -1, 0),
                        new Vector3(xL, yB, t), new Vector3(xR, yB, t),
                        new Vector3(xL, yB, -t), new Vector3(xR, yB, -t), px, py, px + 1, py + 1);
            }

            var part = mesh.IndicesCount > 0 ? new EntityRenderer.PartMesh(mesh) : null;
            mesh.Clear();
            return part;
        }

        private static bool Opaque(TextureData data, int x, int y)
        {
            if (x < 0 || y < 0 || x >= data.Width || y >= data.Height) return false;
            return data.Pixels[(y * data.Width + x) * 4 + 3] >= OpaqueCutoff;
        }
    }
}

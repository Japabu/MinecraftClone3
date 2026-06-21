using System;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Graphics;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    internal static class ChunkMesher
    {
        // Baked into a water face's normal.w; EncodeNormal (n*0.5+0.5) stores it as 0.75 (≈191/255 in Rgba8)
        // in the G-buffer normal alpha — distinct from lit solid (0 → 0.5) and the unlit flag (1 → 1.0) — so
        // Composition.fs can shade water specially. Must stay inside that shader's WaterFlagLo/Hi band; the
        // two constants are a matched pair (see Composition.fs).
        private const float WaterNormalW = 0.5f;

        // Baked directional face shade for LOD relief — the deferred lighting is baked-per-vertex (no N·L term),
        // so vertical skirt faces are darkened here (Minecraft-style) to read as shaded cliff/step sides.
        private const float LodSideShadeNS = 0.8f;   // north/south (Back/Front) skirts
        private const float LodSideShadeEW = 0.6f;   // east/west (Right/Left) skirts


        private static readonly Vector3[] FacePositions = {
            //left
            new Vector3(-0.5f, +0.5f, -0.5f), new Vector3(-0.5f, +0.5f, +0.5f),
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, +0.5f),
            //right
            new Vector3(+0.5f, +0.5f, +0.5f), new Vector3(+0.5f, +0.5f, -0.5f),
            new Vector3(+0.5f, -0.5f, +0.5f), new Vector3(+0.5f, -0.5f, -0.5f),
            //bottom
            new Vector3(-0.5f, -0.5f, +0.5f), new Vector3(+0.5f, -0.5f, +0.5f),
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(+0.5f, -0.5f, -0.5f),
            //top
            new Vector3(-0.5f, +0.5f, -0.5f), new Vector3(+0.5f, +0.5f, -0.5f),
            new Vector3(-0.5f, +0.5f, +0.5f), new Vector3(+0.5f, +0.5f, +0.5f),
            //back
            new Vector3(+0.5f, +0.5f, -0.5f), new Vector3(-0.5f, +0.5f, -0.5f),
            new Vector3(+0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f),
            //front
            new Vector3(-0.5f, +0.5f, +0.5f), new Vector3(+0.5f, +0.5f, +0.5f),
            new Vector3(-0.5f, -0.5f, +0.5f), new Vector3(+0.5f, -0.5f, +0.5f)
        };

        /// <summary>
        /// Surface (heightmap) LOD mesh of a chunk at <paramref name="stride"/> (2, 4, …) — the Distant-Horizons
        /// approach, rendered as a 2.5-D heightmap rather than stride³ voxel cubes. Per stride×stride column it
        /// finds the real surface (topmost opaque-full block or liquid — so it follows the GROUND under trees,
        /// since leaves are cutoff-transparent, with no canopy spikes), and emits a FLAT top quad at that height
        /// plus VERTICAL skirt quads down to each lower neighbour's surface, so cliffs read as walls and steps
        /// don't see through. Each column uses its own surface block's texture/tint (no material smearing across
        /// a water↔land border) and water is emitted as a reflective water quad (distant water still reflects).
        /// Heights are world-sampled so adjacent chunks agree on the shared edge; a cell is owned by the chunk
        /// whose Y range holds its surface, so it's emitted exactly once up the vertical stack. Everything goes
        /// into the opaque <paramref name="vao"/> (no separate sorted pass at distance).
        /// </summary>
        public static void AddBlocksToVaoLod(WorldBase world, Vector3i chunkOrigin, Chunk chunk, MeshBuffer vao, int stride)
        {
            var bottom = chunkOrigin.Y;
            var top = chunkOrigin.Y + Chunk.Size;          // exclusive
            var n = Chunk.Size / stride;                   // cells per axis
            var gN = n + 2;                                 // surface heights for columns c in [-1, n]
            var scanTop = top + stride;
            var scanBottom = bottom - Chunk.Size;          // catch cliffs ~a chunk below for skirts

            // Surface Y per column on a 1-cell-padded grid (int.MinValue = none in window). H(ci,cj) below.
            Span<int> heights = stackalloc int[gN * gN];
            for (var ci = -1; ci <= n; ci++)
            for (var cj = -1; cj <= n; cj++)
                heights[(ci + 1) * gN + (cj + 1)] =
                    SurfaceTop(world, chunkOrigin.X + ci * stride, chunkOrigin.Z + cj * stride, scanTop, scanBottom);

            for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                var sy = heights[(i + 1) * gN + (j + 1)];
                if (sy < bottom || sy >= top) continue;     // ownership: this chunk's Y range holds the surface

                var wx = chunkOrigin.X + i * stride;
                var wz = chunkOrigin.Z + j * stride;
                var pos = new Vector3i(wx, sy, wz);
                var block = world.GetBlock(wx, sy, wz);
                if (block == BlockRegistry.BlockAir || block.Model == null) continue;

                var isWater = block.GetRenderMaterial(world, pos) == RenderMaterial.Water;
                var light = SampleBrightness(world, new Vector3i(wx, sy + 1, wz));   // flat per-column light

                var x0 = wx - 0.5f;
                var x1 = wx + stride - 0.5f;
                var z0 = wz - 0.5f;
                var z1 = wz + stride - 0.5f;
                var yTop = sy + 0.5f;

                // Flat top (order = FacePositions Top: (x-,z-),(x+,z-),(x-,z+),(x+,z+)).
                if (TryGetFace(block, BlockFace.Top, out var topFace))
                    EmitLodQuad(vao, topFace, new Vector4(0, 1, 0, isWater ? WaterNormalW : 0f),
                        Tint(world, block, pos, topFace),
                        new Vector3(x0, yTop, z0), new Vector3(x1, yTop, z0),
                        new Vector3(x0, yTop, z1), new Vector3(x1, yTop, z1), light);

                // Skirts down to each lower neighbour (vert order = that side face's FacePositions: t0,t1 top).
                EmitSkirt(world, vao, block, pos, sy, yTop, light, BlockFace.Right,
                    new Vector3(x1, yTop, z1), new Vector3(x1, yTop, z0), heights[(i + 2) * gN + (j + 1)], scanBottom);
                EmitSkirt(world, vao, block, pos, sy, yTop, light, BlockFace.Left,
                    new Vector3(x0, yTop, z0), new Vector3(x0, yTop, z1), heights[(i) * gN + (j + 1)], scanBottom);
                EmitSkirt(world, vao, block, pos, sy, yTop, light, BlockFace.Front,
                    new Vector3(x0, yTop, z1), new Vector3(x1, yTop, z1), heights[(i + 1) * gN + (j + 2)], scanBottom);
                EmitSkirt(world, vao, block, pos, sy, yTop, light, BlockFace.Back,
                    new Vector3(x1, yTop, z0), new Vector3(x0, yTop, z0), heights[(i + 1) * gN + (j)], scanBottom);
            }
        }

        /// <summary>Topmost solid-or-liquid block Y in the world column within [scanBottom, scanTop] (the LOD
        /// surface — skips cutoff-transparent leaves so trees don't spike the heightmap); int.MinValue if none.</summary>
        private static int SurfaceTop(WorldBase world, int wx, int wz, int scanTop, int scanBottom)
        {
            for (var y = scanTop; y >= scanBottom; y--)
                if (IsLodSurface(world.GetBlock(wx, y, wz), world, new Vector3i(wx, y, wz)))
                    return y;
            return int.MinValue;
        }

        /// <summary>What counts as the LOD heightmap surface. Includes leaves (Cutoff) so trees read as canopy
        /// bumps, not trunk stumps — DH treats any block with presence as a solid coloured LOD voxel. Liquid
        /// counts (distant water surface); true translucent blocks (glass) don't.</summary>
        private static bool IsLodSurface(Block b, WorldBase world, Vector3i pos)
        {
            if (b == BlockRegistry.BlockAir) return false;
            if (b.IsLiquid) return true;
            var t = b.IsTransparent(world, pos);
            if (t == TransparencyType.Cutoff) return true;
            if (t == TransparencyType.Transparent) return false;
            return b.IsOpaqueFullBlock(world, pos);
        }

        private static void EmitSkirt(WorldBase world, MeshBuffer vao, Block block, Vector3i pos, int sy, float yTop,
            Vector4 light, BlockFace face, Vector3 t0, Vector3 t1, int neighbourY, int scanBottom)
        {
            if (neighbourY != int.MinValue && neighbourY >= sy) return;            // neighbour covers the gap
            var yBot = (neighbourY == int.MinValue ? scanBottom : neighbourY) + 0.5f;
            if (yBot >= yTop || !TryGetFace(block, face, out var sideFace)) return;
            var shade = face == BlockFace.Right || face == BlockFace.Left ? LodSideShadeEW : LodSideShadeNS;
            EmitLodQuad(vao, sideFace, new Vector4(face.GetNormal(), 0), Tint(world, block, pos, sideFace),
                t0, t1, new Vector3(t0.X, yBot, t0.Z), new Vector3(t1.X, yBot, t1.Z), light * shade);
        }

        private static void EmitLodQuad(MeshBuffer vao, BlockModel.FaceData face, Vector4 normal, Vector3 tint,
            Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector4 light)
        {
            var tex = face.LoadedTexture;
            var uv = face.GetTexCoords();
            var baseVertex = vao.VertexCount;
            vao.Add(v0, TexCoord(tex, uv[0]), normal, tint, light);
            vao.Add(v1, TexCoord(tex, uv[1]), normal, tint, light);
            vao.Add(v2, TexCoord(tex, uv[2]), normal, tint, light);
            vao.Add(v3, TexCoord(tex, uv[3]), normal, tint, light);
            vao.AddFace(baseVertex, false, Vector3.Zero);
        }

        private static Vector3 Tint(WorldBase world, Block block, Vector3i pos, BlockModel.FaceData face)
            => face.TintIndex == -1 ? new Vector3(1) : block.GetTintColor(world, pos, face.TintIndex).ToVector4().Xyz;

        private static bool TryGetFace(Block block, BlockFace face, out BlockModel.FaceData data)
        {
            foreach (var element in block.Model.Elements)
                if (element.Faces.TryGetValue(face, out data))
                    return true;
            foreach (var element in block.Model.Elements)
            foreach (var entry in element.Faces)
            {
                data = entry.Value;
                return true;
            }
            data = null;
            return false;
        }

        private static Vector4 TexCoord(BlockTexture tex, Vector2 uv)
            => tex == null ? new Vector4(-1) : new Vector4(uv) {Z = tex.TextureId, W = tex.ArrayId};

        public static void AddBlockToVao(WorldBase world, Vector3i blockPos, int x, int y, int z, Block block,
            MeshBuffer vao, MeshBuffer transparentVao)
        {
            //If block is invisible or does not have a model for some reason ignore it
            if (!block.IsVisible(world, blockPos) || block.Model == null) return;

            // Block-state orientation (e.g. a stair's facing) applied after the element transform, so it
            // rotates the centred element about the block origin. Identity for every normal block.
            var orient = block.GetModelTransform(world, blockPos);

            foreach (var element in block.Model.Elements)
            {
                var transform = Matrix4.CreateScale((element.To - element.From) / 16) *
                                Matrix4.CreateTranslation((element.To - element.From) / 32 + element.From / 16) *
                                Matrix4.CreateTranslation(new Vector3(-0.5f)) *
                                orient;

                foreach (var entry in element.Faces)
                {
                    var noCull = entry.Value.Cullface == BlockFace.None;

                    var face = entry.Key;
                    var cullface = noCull ? face : entry.Value.Cullface;
                    var otherBlockPos = blockPos + cullface.GetNormali();
                    var otherBlock = world.GetBlock(otherBlockPos);

                    var fullBlock = block.IsFullBlock(world, blockPos);
                    var transparency = block.IsTransparent(world, blockPos);

                    var otherFullBlock = otherBlock.IsFullBlock(world, otherBlockPos);
                    var otherTransparency = otherBlock.IsTransparent(world, otherBlockPos);

                    var connectionType = block.ConnectsToBlock(world, blockPos, otherBlockPos, otherBlock);

                    if (connectionType == ConnectionType.Connected) continue;

                    if (!noCull && connectionType == ConnectionType.Undefined && otherBlock.IsVisible(world, otherBlockPos) &&
                        otherTransparency == TransparencyType.None && fullBlock && otherFullBlock) continue;

                    AddFaceToVao(world, blockPos, x, y, z, block, face, entry.Value,
                        transparency == TransparencyType.Transparent ? transparentVao : vao, transform);
                }
            }
        }

        public static void AddFaceToVao(WorldBase world, Vector3i blockPos, int x, int y, int z, Block block, BlockFace face, BlockModel.FaceData data, MeshBuffer vao, Matrix4 transform, Vector4? lodLight = null)
        {
            var faceId = (int) face - 1;
            var baseVertex = vao.VertexCount;

            var texture = data.LoadedTexture;
            var texCoords = data.GetTexCoords();
            var color = data.TintIndex == -1 ? new Vector4(1) : block.GetTintColor(world, blockPos, data.TintIndex).ToVector4();
            var normal = new Vector4(face.GetNormal());
            if (block.GetRenderMaterial(world, blockPos) == RenderMaterial.Water) normal.W = WaterNormalW;
            var colorXyz = color.Xyz;

            if (texCoords.Length != 4) throw new Exception($"\"{block}\" invalid texture coords array length!");

            var sorted = vao is SortedVertexArrayObject;
            var faceMiddle = Vector3.Zero;

            //per vertex light value interpolation (smooth lighting + free ambient occlusion); the four
            //corner brightnesses are kept to flip the quad for AO anisotropy. xyz = block light, w = sky.
            //https://0fps.net/2013/07/03/ambient-occlusion-for-minecraft-like-worlds/
            Vector4 b0 = default, b1 = default, b2 = default, b3 = default;

            for (var j = 0; j < 4; j++)
            {
                var vertexPosition = FacePositions[faceId * 4 + j];
                // Bake WORLD-space position (chunk origin folded in) so the renderer needs no per-chunk model
                // matrix — that's what lets every chunk's opaque mesh share one buffer + one batched multidraw.
                var position = (new Vector4(vertexPosition, 1) * transform).Xyz + blockPos.ToVector3();

                //tex coords are -1 if texture is null; texCoord z = texId, w = textureArrayId
                var texCoord = texture == null ? new Vector4(-1) : new Vector4(texCoords[j]) {Z = texture.TextureId, W = texture.ArrayId};

                // LOD faces light flat from the exposed air super-block (per-vertex AO sampling would read the
                // immediate neighbour, which is inside the solid super-block region → black faces).
                var brightness = lodLight ?? CalculateBrightness(world, block, blockPos, face, vertexPosition);

                vao.Add(position, texCoord, normal, colorXyz, brightness);

                if (sorted) faceMiddle += position;
                switch (j)
                {
                    case 0: b0 = brightness; break;
                    case 1: b1 = brightness; break;
                    case 2: b2 = brightness; break;
                    default: b3 = brightness; break;
                }
            }

            var flipped = (b0 + b3).LengthSquared > (b1 + b2).LengthSquared;

            // position is already world-space, so the accumulated faceMiddle/4 is the world-space face centre.
            if (sorted)
                faceMiddle /= 4;

            vao.AddFace(baseVertex, flipped, faceMiddle);
        }

        // xyz = block-light brightness (per channel), w = sky-light brightness. Block and sky are sampled
        // together at each position so the (expensive) neighbour smooth-lighting walk happens once.
        private static Vector4 SampleBrightness(WorldBase world, Vector3i pos)
        {
            var rgb = LightLevelToBrightness(world.GetBlockLightLevel(pos).Vector3);
            var sky = CustomLightLevelToBrightness(world.GetSkyLight(pos));
            return new Vector4(rgb, sky);
        }

        private static Vector4 CalculateBrightness(WorldBase world, Block block, Vector3i blockPos, BlockFace face, Vector3 vertexPosition)
        {
            //if its not a full opaque block return brightness of itself
            if (!block.IsOpaqueFullBlock(world, blockPos) || !block.Model.AmbientOcclusion)
                return SampleBrightness(world, blockPos);

            //TODO: smooth lighting setting
            //return LightLevelToBrightness(world.GetBlockLightLevel(blockPos + face.GetNormali()));

            var normal = face.GetNormali();
            var pos = blockPos + normal;
            var offset = (vertexPosition * 2).ToVector3i();

            // Strip the vertex offset's component along the face normal, leaving the two tangential
            // components. For a real face corner those are both +-1, so length squared == 2.
            // Note: subtract (offset * normal*normal), not (normal*normal): normal*normal is always a
            // positive axis mask, so on negative-normal faces (left/bottom/back) it left a -2 on the
            // normal axis and every vertex was wrongly treated as a non-corner, leaving those faces unlit.
            if ((offset - offset * (normal * normal)).LengthSquared != 2)
            {
                //If vertex is not a corner do not apply ambient occlusion but apply the blocks own brightness
                return SampleBrightness(world, blockPos);
            }

            if (normal.X != 0)
            {
                return GetSmoothLightValue(world, pos, pos + new Vector3i(0, offset.Y, 0),
                    pos + new Vector3i(0, 0, offset.Z), pos + new Vector3i(0, offset.Y, offset.Z));
            }
            if (normal.Y != 0)
            {
                return GetSmoothLightValue(world, pos, pos + new Vector3i(offset.X, 0, 0),
                    pos + new Vector3i(0, 0, offset.Z), pos + new Vector3i(offset.X, 0, offset.Z));
            }
            if (normal.Z != 0)
            {
                return GetSmoothLightValue(world, pos, pos + new Vector3i(offset.X, 0, 0),
                    pos + new Vector3i(0, offset.Y, 0), pos + new Vector3i(offset.X, offset.Y, 0));
            }

            throw new Exception("Something is really broken if you can read this :S");
        }

        private static Vector4 GetSmoothLightValue(WorldBase world, Vector3i p0, Vector3i p1, Vector3i p2, Vector3i p3)
        {
            var lightValue = SampleBrightness(world, p0);

            lightValue += SampleBrightness(world, p1);
            lightValue += SampleBrightness(world, p2);

            //If two full blocks obstruct the corner ignore the it
            if (world.IsFullBlock(p1) && world.IsFullBlock(p2))
                return lightValue / 3;

            lightValue += SampleBrightness(world, p3);
            return lightValue / 4;
        }

        // Falloff per light level: brightness = Base^(15 - level). Base must be < 1 for darker
        // areas to actually look darker; Base = 1 flattens every level to full brightness.
        private const float Base = 0.8f;
        //private const float CustomBase = 0.897499991f;

        private static Vector3 LightLevelToBrightness(Vector3 lightLevel)
        {
            for (var i = 0; i < 3; i++)
                lightLevel[i] = CustomLightLevelToBrightness(lightLevel[i]);

            return lightLevel;
        }


        private static float VanillaLightLevelToBrightness(float lightLevel)
            => (float) Math.Pow(Base, Math.Max(15 - lightLevel, 0));

        private static float CustomLightLevelToBrightness(float lightLevel)
        {
            return VanillaLightLevelToBrightness(lightLevel);

            //return (float)Math.Pow(CustomBase, Math.Max(31 - lightLevel, 0));
            //return (float)Math.Pow(0.8, 1 - (lightLevel - 14) / 17);
        }
    }
}
using System;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Graphics;
using Silk.NET.Maths;

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


        private static readonly Vector3D<float>[] FacePositions = {
            //left
            new Vector3D<float>(-0.5f, +0.5f, -0.5f), new Vector3D<float>(-0.5f, +0.5f, +0.5f),
            new Vector3D<float>(-0.5f, -0.5f, -0.5f), new Vector3D<float>(-0.5f, -0.5f, +0.5f),
            //right
            new Vector3D<float>(+0.5f, +0.5f, +0.5f), new Vector3D<float>(+0.5f, +0.5f, -0.5f),
            new Vector3D<float>(+0.5f, -0.5f, +0.5f), new Vector3D<float>(+0.5f, -0.5f, -0.5f),
            //bottom
            new Vector3D<float>(-0.5f, -0.5f, +0.5f), new Vector3D<float>(+0.5f, -0.5f, +0.5f),
            new Vector3D<float>(-0.5f, -0.5f, -0.5f), new Vector3D<float>(+0.5f, -0.5f, -0.5f),
            //top
            new Vector3D<float>(-0.5f, +0.5f, -0.5f), new Vector3D<float>(+0.5f, +0.5f, -0.5f),
            new Vector3D<float>(-0.5f, +0.5f, +0.5f), new Vector3D<float>(+0.5f, +0.5f, +0.5f),
            //back
            new Vector3D<float>(+0.5f, +0.5f, -0.5f), new Vector3D<float>(-0.5f, +0.5f, -0.5f),
            new Vector3D<float>(+0.5f, -0.5f, -0.5f), new Vector3D<float>(-0.5f, -0.5f, -0.5f),
            //front
            new Vector3D<float>(-0.5f, +0.5f, +0.5f), new Vector3D<float>(+0.5f, +0.5f, +0.5f),
            new Vector3D<float>(-0.5f, -0.5f, +0.5f), new Vector3D<float>(+0.5f, -0.5f, +0.5f)
        };

        private static void EmitSkirt(WorldBase world, MeshBuffer vao, Block block, Vector3D<int> pos, int sy, float yTop,
            Vector4D<float> light, BlockFace face, Vector3D<float> t0, Vector3D<float> t1, int neighbourY, int scanBottom)
        {
            if (neighbourY != int.MinValue && neighbourY >= sy) return;            // neighbour covers the gap
            var yBot = (neighbourY == int.MinValue ? scanBottom : neighbourY) + 1f;
            if (yBot >= yTop || !TryGetFace(block, face, out var sideFace)) return;
            var shade = face == BlockFace.Right || face == BlockFace.Left ? LodSideShadeEW : LodSideShadeNS;
            EmitLodQuad(vao, sideFace, new Vector4D<float>(face.GetNormal(), 0), Tint(world, block, pos, sideFace),
                t0, t1, new Vector3D<float>(t0.X, yBot, t0.Z), new Vector3D<float>(t1.X, yBot, t1.Z), light * shade);
        }

        private static void EmitLodQuad(MeshBuffer vao, BlockModel.FaceData face, Vector4D<float> normal, Vector3D<float> tint,
            Vector3D<float> v0, Vector3D<float> v1, Vector3D<float> v2, Vector3D<float> v3, Vector4D<float> light)
        {
            var tex = face.LoadedTexture;
            var uv = face.GetTexCoords();
            var baseVertex = vao.VertexCount;
            vao.Add(v0, TexCoord(tex, uv[0]), normal, tint, light);
            vao.Add(v1, TexCoord(tex, uv[1]), normal, tint, light);
            vao.Add(v2, TexCoord(tex, uv[2]), normal, tint, light);
            vao.Add(v3, TexCoord(tex, uv[3]), normal, tint, light);
            vao.AddFace(baseVertex, false, Vector3D<float>.Zero);
        }

        private static Vector3D<float> Tint(WorldBase world, Block block, Vector3D<int> pos, BlockModel.FaceData face)
        {
            if (face.TintIndex == -1) return new Vector3D<float>(1);
            var c = block.GetTintColor(world, pos, face.TintIndex);
            return new Vector3D<float>(c.X, c.Y, c.Z);
        }

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

        private static Vector4D<float> TexCoord(BlockTexture tex, Vector2D<float> uv)
            => tex == null ? new Vector4D<float>(-1) : new Vector4D<float>(uv.X, uv.Y, 0f, 0f) {Z = tex.TextureId, W = tex.ArrayId};

        // Floor a Phase-2 region skirt drops to when its neighbour column is unknown (region not streamed):
        // below bedrock, so the wall is hidden underground / behind the horizon fog. The over-long wall is
        // invisible (covered by neighbours when they load, or fogged out).
        private const int LodFloorY = -40;

        /// <summary>
        /// Phase-2 distant-horizon mesher: builds the flat-top + skirt heightmap for one streamed LOD region
        /// directly from its packed column run-list (no world, no real blocks) — the same emission as
        /// <see cref="AddBlocksToVaoLod"/> but driven by the store instead of sampling a chunk. Block id, surface
        /// Y and sky come from the packed column; light is sky-only (no torches at the horizon); tint/material use
        /// the block's world-free properties (grass/water tint by position, water reflection flag). Neighbour
        /// heights for skirts read across into the adjacent regions (so region borders don't crack); an unknown
        /// neighbour skirts to <see cref="LodFloorY"/>.
        /// </summary>
        public static void AddLodColumnRegionToVao(LodColumnStore store, Vector3D<int> regionKey, MeshBuffer vao, int meshStep)
        {
            if (!store.TryGetRegion(regionKey, out var region)) return;
            var cols = region.Columns;
            var superN = LodColumn.CellsPerAxis / meshStep;     // super-cells per axis (stride-ring coarsening)
            var cellSize = LodColumn.Stride * meshStep;          // world blocks per super-cell
            var baseX = regionKey.X << 7;
            var baseZ = regionKey.Z << 7;

            store.TryGetRegion(regionKey + new Vector3D<int>(1, 0, 0), out var rPosX);
            store.TryGetRegion(regionKey + new Vector3D<int>(-1, 0, 0), out var rNegX);
            store.TryGetRegion(regionKey + new Vector3D<int>(0, 0, 1), out var rPosZ);
            store.TryGetRegion(regionKey + new Vector3D<int>(0, 0, -1), out var rNegZ);

            // Downsample the stride-2 store to the super-grid by MAX surface (keeps trees/peaks from sinking when
            // coarsening — a super-cell shows its tallest member, so forests stay as canopy plateaus). meshStep 1
            // is the identity (super-grid == store, the near horizon ring's stride-2); 2/4/8 are the stride-4/8/16 rings.
            Span<long> grid = stackalloc long[superN * superN];
            for (var scx = 0; scx < superN; scx++)
            for (var scz = 0; scz < superN; scz++)
                grid[scx * superN + scz] = MaxSurfaceCell(cols, scx, scz, meshStep);

            // Greedy-merged flat tops over the super-grid (water/plains collapse to a few big quads).
            Span<bool> used = stackalloc bool[superN * superN];
            for (var scx = 0; scx < superN; scx++)
            for (var scz = 0; scz < superN; scz++)
            {
                var idx = scx * superN + scz;
                if (used[idx]) continue;
                var packed = grid[idx];
                if (LodColumn.IsEmpty(packed)) { used[idx] = true; continue; }
                var block = GameRegistry.BlockRegistry[LodColumn.BlockId(packed)];
                if (block == BlockRegistry.BlockAir || block.Model == null ||
                    !TryGetFace(block, BlockFace.Top, out var topFace)) { used[idx] = true; continue; }

                var w = 1;
                while (scz + w < superN && !used[idx + w] && grid[idx + w] == packed) w++;
                var h = 1;
                var grow = true;
                while (scx + h < superN && grow)
                {
                    for (var k = 0; k < w; k++)
                        if (used[(scx + h) * superN + scz + k] || grid[(scx + h) * superN + scz + k] != packed) { grow = false; break; }
                    if (grow) h++;
                }
                for (var i = 0; i < h; i++)
                for (var k = 0; k < w; k++)
                    used[(scx + i) * superN + scz + k] = true;

                var sy = LodColumn.SurfaceY(packed);
                var pos = new Vector3D<int>(baseX + scx * cellSize, sy, baseZ + scz * cellSize);
                var isWater = block.GetRenderMaterial(null, pos) == RenderMaterial.Water;
                var light = new Vector4D<float>(0, 0, 0, CustomLightLevelToBrightness(LodColumn.Sky(packed)));
                var x0 = baseX + scx * cellSize;
                var x1 = baseX + (scx + h) * cellSize;
                var z0 = baseZ + scz * cellSize;
                var z1 = baseZ + (scz + w) * cellSize;
                var yTop = sy + 1f;
                EmitLodQuad(vao, topFace, new Vector4D<float>(0, 1, 0, isWater ? WaterNormalW : 0f),
                    Tint(null, block, pos, topFace),
                    new Vector3D<float>(x0, yTop, z0), new Vector3D<float>(x1, yTop, z0),
                    new Vector3D<float>(x0, yTop, z1), new Vector3D<float>(x1, yTop, z1), light);
            }

            for (var scx = 0; scx < superN; scx++)
            for (var scz = 0; scz < superN; scz++)
            {
                var packed = grid[scx * superN + scz];
                if (LodColumn.IsEmpty(packed)) continue;
                var block = GameRegistry.BlockRegistry[LodColumn.BlockId(packed)];
                if (block == BlockRegistry.BlockAir || block.Model == null) continue;

                var sy = LodColumn.SurfaceY(packed);
                var pos = new Vector3D<int>(baseX + scx * cellSize, sy, baseZ + scz * cellSize);
                var light = new Vector4D<float>(0, 0, 0, CustomLightLevelToBrightness(LodColumn.Sky(packed)));
                var x0 = baseX + scx * cellSize;
                var x1 = baseX + (scx + 1) * cellSize;
                var z0 = baseZ + scz * cellSize;
                var z1 = baseZ + (scz + 1) * cellSize;
                var yTop = sy + 1f;

                EmitSkirt(null, vao, block, pos, sy, yTop, light, BlockFace.Right,
                    new Vector3D<float>(x1, yTop, z1), new Vector3D<float>(x1, yTop, z0),
                    SuperNeighbourY(grid, superN, rPosX, scx + 1, scz, meshStep), LodFloorY);
                EmitSkirt(null, vao, block, pos, sy, yTop, light, BlockFace.Left,
                    new Vector3D<float>(x0, yTop, z0), new Vector3D<float>(x0, yTop, z1),
                    SuperNeighbourY(grid, superN, rNegX, scx - 1, scz, meshStep), LodFloorY);
                EmitSkirt(null, vao, block, pos, sy, yTop, light, BlockFace.Front,
                    new Vector3D<float>(x0, yTop, z1), new Vector3D<float>(x1, yTop, z1),
                    SuperNeighbourY(grid, superN, rPosZ, scx, scz + 1, meshStep), LodFloorY);
                EmitSkirt(null, vao, block, pos, sy, yTop, light, BlockFace.Back,
                    new Vector3D<float>(x1, yTop, z0), new Vector3D<float>(x0, yTop, z0),
                    SuperNeighbourY(grid, superN, rNegZ, scx, scz - 1, meshStep), LodFloorY);
            }
        }

        /// <summary>Max-surface packed column over a meshStep×meshStep block of stride-2 store cells (the
        /// super-cell downsample), or 0 (empty) if all empty.</summary>
        private static long MaxSurfaceCell(long[] cols, int scx, int scz, int meshStep)
        {
            long best = 0;
            var bestY = int.MinValue;
            var cx0 = scx * meshStep;
            var cz0 = scz * meshStep;
            for (var i = 0; i < meshStep; i++)
            for (var j = 0; j < meshStep; j++)
            {
                var packed = cols[(cx0 + i) * LodColumn.CellsPerAxis + (cz0 + j)];
                if (LodColumn.IsEmpty(packed)) continue;
                var y = LodColumn.SurfaceY(packed);
                if (y > bestY) { bestY = y; best = packed; }
            }
            return best;
        }

        /// <summary>Surface Y of the neighbour super-cell (scx,scz): the self super-grid when in range, else the
        /// adjacent region's wrapped super-cell (max-surface on demand at the same step); int.MinValue if absent.</summary>
        private static int SuperNeighbourY(Span<long> grid, int superN, LodColumn neighbour, int scx, int scz, int meshStep)
        {
            long packed;
            if (scx >= 0 && scx < superN && scz >= 0 && scz < superN)
                packed = grid[scx * superN + scz];
            else
            {
                if (neighbour == null) return int.MinValue;
                packed = MaxSurfaceCell(neighbour.Columns, (scx + superN) % superN, (scz + superN) % superN, meshStep);
            }
            return LodColumn.IsEmpty(packed) ? int.MinValue : LodColumn.SurfaceY(packed);
        }

        public static void AddBlockToVao(WorldBase world, Vector3D<int> blockPos, int x, int y, int z, Block block,
            MeshBuffer vao, MeshBuffer transparentVao, Vector3D<float> originOffset = default)
        {
            //If block is invisible or does not have a model for some reason ignore it
            var (model, orient) = block.GetRenderModel(world, blockPos);
            if (!block.IsVisible(world, blockPos) || model == null) return;

            // Block-state orientation (a stair's facing, a furnace's blockstate variant rotation) applied after
            // the element transform: the -0.5 shift centres the element on the block origin so `orient` rotates
            // about the block centre, then the +0.5 shift moves it into the [0,1] cell so block P fills
            // [P, P+1] (corner-origin, matching Minecraft). Identity orient for every normal block.

            foreach (var element in model.Elements)
            {
                var transform = Matrix4X4.CreateScale((element.To - element.From) / 16) *
                                Matrix4X4.CreateTranslation((element.To - element.From) / 32 + element.From / 16) *
                                Matrix4X4.CreateTranslation(new Vector3D<float>(-0.5f)) *
                                orient *
                                Matrix4X4.CreateTranslation(new Vector3D<float>(0.5f));

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
                        transparency == TransparencyType.Transparent ? transparentVao : vao, transform, originOffset);
                }
            }
        }

        public static void AddFaceToVao(WorldBase world, Vector3D<int> blockPos, int x, int y, int z, Block block, BlockFace face, BlockModel.FaceData data, MeshBuffer vao, Matrix4X4<float> transform, Vector3D<float> originOffset = default, Vector4D<float>? lodLight = null)
        {
            var faceId = (int) face - 1;
            var baseVertex = vao.VertexCount;

            var texture = data.LoadedTexture;
            var texCoords = data.GetTexCoords();
            var color = data.TintIndex == -1 ? new Vector4D<float>(1) : block.GetTintColor(world, blockPos, data.TintIndex);
            var fn = face.GetNormal(); var normal = new Vector4D<float>(fn.X, fn.Y, fn.Z, 0f);
            if (block.GetRenderMaterial(world, blockPos) == RenderMaterial.Water) normal.W = WaterNormalW;
            var colorXyz = new Vector3D<float>(color.X, color.Y, color.Z);

            if (texCoords.Length != 4) throw new Exception($"\"{block}\" invalid texture coords array length!");

            var sorted = vao.Sorted;
            var faceMiddle = Vector3D<float>.Zero;

            //per vertex light value interpolation (smooth lighting + free ambient occlusion); the four
            //corner brightnesses are kept to flip the quad for AO anisotropy. xyz = block light, w = sky.
            //https://0fps.net/2013/07/03/ambient-occlusion-for-minecraft-like-worlds/
            Vector4D<float> b0 = default, b1 = default, b2 = default, b3 = default;

            for (var j = 0; j < 4; j++)
            {
                var vertexPosition = FacePositions[faceId * 4 + j];
                // Bake WORLD-space position (chunk origin folded in) so the renderer needs no per-chunk model
                // matrix — that's what lets every chunk's opaque mesh share one buffer + one batched multidraw.
                var transformed = new Vector4D<float>(vertexPosition, 1) * transform;
                var position = new Vector3D<float>(transformed.X, transformed.Y, transformed.Z) + blockPos.ToVector3() + originOffset;

                //tex coords are -1 if texture is null; texCoord z = texId, w = textureArrayId
                var texCoord = texture == null ? new Vector4D<float>(-1) : new Vector4D<float>(texCoords[j].X, texCoords[j].Y, 0f, 0f) {Z = texture.TextureId, W = texture.ArrayId};

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
        private static Vector4D<float> SampleBrightness(WorldBase world, Vector3D<int> pos)
        {
            var rgb = LightLevelToBrightness(world.GetBlockLightLevel(pos).Vector3);
            var sky = CustomLightLevelToBrightness(world.GetSkyLight(pos));
            return new Vector4D<float>(rgb, sky);
        }

        private static Vector4D<float> CalculateBrightness(WorldBase world, Block block, Vector3D<int> blockPos, BlockFace face, Vector3D<float> vertexPosition)
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
                return GetSmoothLightValue(world, pos, pos + new Vector3D<int>(0, offset.Y, 0),
                    pos + new Vector3D<int>(0, 0, offset.Z), pos + new Vector3D<int>(0, offset.Y, offset.Z));
            }
            if (normal.Y != 0)
            {
                return GetSmoothLightValue(world, pos, pos + new Vector3D<int>(offset.X, 0, 0),
                    pos + new Vector3D<int>(0, 0, offset.Z), pos + new Vector3D<int>(offset.X, 0, offset.Z));
            }
            if (normal.Z != 0)
            {
                return GetSmoothLightValue(world, pos, pos + new Vector3D<int>(offset.X, 0, 0),
                    pos + new Vector3D<int>(0, offset.Y, 0), pos + new Vector3D<int>(offset.X, offset.Y, 0));
            }

            throw new Exception("Something is really broken if you can read this :S");
        }

        private static Vector4D<float> GetSmoothLightValue(WorldBase world, Vector3D<int> p0, Vector3D<int> p1, Vector3D<int> p2, Vector3D<int> p3)
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

        private static Vector3D<float> LightLevelToBrightness(Vector3D<float> lightLevel)
        {
            lightLevel.X = CustomLightLevelToBrightness(lightLevel.X);
            lightLevel.Y = CustomLightLevelToBrightness(lightLevel.Y);
            lightLevel.Z = CustomLightLevelToBrightness(lightLevel.Z);

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
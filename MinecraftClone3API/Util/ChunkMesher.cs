using System;
using System.Linq;
using MinecraftClone3API.Blocks;
using MinecraftClone3API.Graphics;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Util
{
    internal static class ChunkMesher
    {
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

        private static readonly Vector2[] FaceTexCoords =
        {
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(0, 1), new Vector2(1, 1),
        };

        private static readonly uint[] FaceIndices = {2, 1, 0, 2, 3, 1};
        private static readonly uint[] FlippedFaceIndices = { 0, 2, 3, 0, 3, 1 };

        public static void AddBlockToVao(WorldServer world, Vector3i blockPos, int x, int y, int z, Block block,
            VertexArrayObject vao, VertexArrayObject transparentVao)
        {
            //If block is invisible or does not have a model for some reason ignore it
            if (!block.IsVisible(world, blockPos) || block.Model == null) return;

            foreach (var element in block.Model.Elements)
            {
                var transform = Matrix4.CreateScale((element.To - element.From) / 16) *
                                Matrix4.CreateTranslation((element.To - element.From) / 32 + element.From / 16) *
                                Matrix4.CreateTranslation(new Vector3(-0.5f));

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

        public static void AddFaceToVao(WorldServer world, Vector3i blockPos, int x, int y, int z, Block block, BlockFace face, BlockModel.FaceData data, VertexArrayObject vao, Matrix4 transform)
        {
            var faceId = (int) face - 1;
            var indicesOffset = vao.VertexCount;

            //var transform = Matrix4.CreateScale()//Matrix4.Identity;//block.GetTransform(world, blockPos, face);
            var texture = data.LoadedTexture;//block.GetTexture(world, blockPos, face);
            //var overlayTexture = block.GetOverlayTexture(world, blockPos, face);
            var texCoords = data.GetTexCoords();//block.GetTexCoords(world, blockPos, face) ?? FaceTexCoords;
            //var overlayTexCoords = block.GetOverlayTexCoords(world, blockPos, face) ?? FaceTexCoords;
            var color = data.TintIndex == -1 ? new Vector4(1) : block.GetTintColor(world, blockPos, data.TintIndex).ToVector4();
            var normal = face.GetNormal();

            if(texCoords.Length != 4) throw new Exception($"\"{block}\" invalid texture coords array length!");


            var vPositions = new Vector3[4];
            var vTexCoords = new Vector4[4];
            var vOverlayTexCoords = new Vector4[4]; //TODO: Remove
            var vBrightness = new Vector3[4];

            for (var j = 0; j < 4; j++)
            {
                var vertexPosition = FacePositions[faceId * 4 + j];
                var position = (new Vector4(vertexPosition, 1) * transform).Xyz + new Vector3(x, y, z);

                //tex coords are -1 if texture is null
                var texCoord = texture == null ? new Vector4(-1) : new Vector4(texCoords[j])
                {
                    //texCoord z = texId, w = textureArrayId
                    Z = texture.TextureId,
                    W = texture.ArrayId
                };



                //per vertex light value interpolation (smooth lighting + free ambient occlusion)
                var brightness = CalculateBrightness(world, block, blockPos, face, vertexPosition);

                //TODO: transform normals

                vPositions[j] = position;
                vTexCoords[j] = texCoord;
                vOverlayTexCoords[j] = new Vector4(-1);
                vBrightness[j] = brightness;
            }

            //Flip faces to fix ambient occlusion anisotrophy
            //https://0fps.net/2013/07/03/ambient-occlusion-for-minecraft-like-worlds/

            for (var j = 0; j < 4; j++)
                vao.Add(vPositions[j], vTexCoords[j], new Vector4(normal), color.Xyz, vBrightness[j]);

            var newIndices = new uint[FaceIndices.Length];

            if ((vBrightness[0] + vBrightness[3]).LengthSquared > (vBrightness[1] + vBrightness[2]).LengthSquared)
            {
                for (var j = 0; j < newIndices.Length; j++)
                    newIndices[j] = (uint)(FlippedFaceIndices[j] + indicesOffset);
            }
            else
            {
                for (var j = 0; j < newIndices.Length; j++)
                    newIndices[j] = (uint)(FaceIndices[j] + indicesOffset);
            }

            //Calculate face middle for transparency sorting
            var faceMiddle = Vector3.Zero;

            if (vao is SortedVertexArrayObject)
            {
                faceMiddle = vPositions.Aggregate(faceMiddle, (current, pos) => current + pos);
                faceMiddle = faceMiddle / vPositions.Length + blockPos.ToVector3() - new Vector3(x, y, z);
            }

            vao.AddFace(newIndices, faceMiddle);
        }

        private static Vector3 CalculateBrightness(WorldServer world, Block block, Vector3i blockPos, BlockFace face, Vector3 vertexPosition)
        {
            //if its not a full opaque block return brightness of itself
            if (!block.IsOpaqueFullBlock(world, blockPos) || !block.Model.AmbientOcclusion)
                return LightLevelToBrightness(world.GetBlockLightLevel(blockPos).Vector3);

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
                return LightLevelToBrightness(world.GetBlockLightLevel(blockPos).Vector3);
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

        private static Vector3 GetSmoothLightValue(WorldServer world, Vector3i p0, Vector3i p1, Vector3i p2, Vector3i p3)
        {
            var lightValue = Vector3.Zero;

            lightValue += LightLevelToBrightness(world.GetBlockLightLevel(p0).Vector3);

            var l0 = LightLevelToBrightness(world.GetBlockLightLevel(p1).Vector3);
            var l1 = LightLevelToBrightness(world.GetBlockLightLevel(p2).Vector3);

            lightValue += l0;
            lightValue += l1;

            //If two full blocks obstruct the corner ignore the it
            if (world.IsFullBlock(p1) && world.IsFullBlock(p2))
                return lightValue / 3;
            
            lightValue += LightLevelToBrightness(world.GetBlockLightLevel(p3).Vector3);
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
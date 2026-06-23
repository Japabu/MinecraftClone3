using System.Collections.Generic;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// CPU-side vertex/index accumulator a chunk mesh is built into (by <see cref="MinecraftClone3API.Util.
    /// ChunkMesher"/> on the mesh thread). Holds no GL state, so building it is safe off the render thread.
    /// The opaque path uploads these lists into the shared <see cref="ChunkMeshArena"/>; the transparent path
    /// keeps a per-chunk <see cref="VertexArrayObject"/> (which derives from this and adds the GL buffers).
    ///
    /// <para>Vertices are <b>packed to 32 bytes</b> (from a former 72) — position float3 (12), uv float2 (8),
    /// a packed uint (4: texId/arrayId/normal-index/material), tint RGBA8 (4), light RGBA8 (4). Voxel normals
    /// are exactly the 6 axes so an index is lossless; tint and light are 0..1 and the G-buffer stores them as
    /// RGBA8 anyway, so 8-bit here is lossless too. Halving the vertex roughly halves geometry-pass vertex
    /// bandwidth (the bottleneck at high render distance) and the mesh-thread allocation. The vertex shader
    /// unpacks back to the same varyings, so the fragment shader is unchanged.</para>
    /// Backing lists are rented from <see cref="VaoBufferPool"/> so a remesh allocates nothing steady-state.
    /// </summary>
    public class MeshBuffer
    {
        protected static readonly uint[] FaceIndices = {2, 1, 0, 2, 3, 1};
        protected static readonly uint[] FlippedFaceIndices = {0, 2, 3, 0, 3, 1};

        public List<Vector3> Positions;   // attrib 0: 3 x float
        public List<Vector2> Uvs;         // attrib 1: 2 x float
        public List<uint> Packed;         // attrib 2: 1 x uint  (texId<<0 | arrayId<<16 | normalIndex<<18 | material<<21)
        public List<uint> Colors;         // attrib 3: RGBA8 tint (normalized ubyte4)
        public List<uint> Lights;         // attrib 4: RGBA8 light (rgb = block light, a = sky factor)
        public List<uint> Indices;

        public int VertexCount => (Positions?.Count).GetValueOrDefault();
        public int IndicesCount => (Indices?.Count).GetValueOrDefault();

        public virtual void Add(Vector3 position, Vector4 texCoord, Vector4 normal, Vector3 color, Vector4 light)
        {
            if (Positions == null)
            {
                Positions = VaoBufferPool.RentVector3();
                Uvs = VaoBufferPool.RentVector2();
                Packed = VaoBufferPool.RentUint();
                Colors = VaoBufferPool.RentUint();
                Lights = VaoBufferPool.RentUint();

                Indices = VaoBufferPool.RentUint();
            }

            Positions.Add(position);
            Uvs.Add(texCoord.Xy);
            Packed.Add(PackVertex(texCoord, normal));
            Colors.Add(PackRgba(color.X, color.Y, color.Z, 1f));
            Lights.Add(PackRgba(light.X, light.Y, light.Z, light.W));
        }

        private static uint PackVertex(Vector4 texCoord, Vector4 normal)
        {
            // texCoord.z = texId, .w = arrayId (set by the mesher; -1 when there is no texture).
            var texId = (uint) ((int) texCoord.Z & 0xFFFF);
            var arrayId = (uint) ((int) texCoord.W & 0x3);
            // Voxel normals are one of the 6 axes; map to an index the VS expands back to the exact axis.
            uint ni;
            if (normal.X > 0.5f) ni = 0;
            else if (normal.X < -0.5f) ni = 1;
            else if (normal.Y > 0.5f) ni = 2;
            else if (normal.Y < -0.5f) ni = 3;
            else if (normal.Z > 0.5f) ni = 4;
            else ni = 5;
            // Material flag (the old normal.w): 0 = lit solid, 0.5 = water, 1 = unlit.
            uint mat = normal.W >= 0.99f ? 2u : normal.W >= 0.25f ? 1u : 0u;
            return texId | (arrayId << 16) | (ni << 18) | (mat << 21);
        }

        private static uint PackRgba(float r, float g, float b, float a)
        {
            uint R = (uint) (Clamp01(r) * 255f + 0.5f);
            uint G = (uint) (Clamp01(g) * 255f + 0.5f);
            uint B = (uint) (Clamp01(b) * 255f + 0.5f);
            uint A = (uint) (Clamp01(a) * 255f + 0.5f);
            return R | (G << 8) | (B << 16) | (A << 24);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        public virtual void AddFace(uint[] indices, Vector3 faceMiddle) => Indices.AddRange(indices);

        /// <summary>Appends the six indices for the four vertices just <see cref="Add"/>ed, generated
        /// in place from the shared winding pattern so the mesher allocates no per-face index array.</summary>
        public virtual void AddFace(int baseVertex, bool flipped, Vector3 faceMiddle)
        {
            var pattern = flipped ? FlippedFaceIndices : FaceIndices;
            for (var i = 0; i < pattern.Length; i++)
                Indices.Add((uint) (pattern[i] + baseVertex));
        }

        public virtual void Clear()
        {
            VaoBufferPool.Return(Positions);
            VaoBufferPool.Return(Uvs);
            VaoBufferPool.Return(Packed);
            VaoBufferPool.Return(Colors);
            VaoBufferPool.Return(Lights);
            VaoBufferPool.Return(Indices);

            Positions = null;
            Uvs = null;
            Packed = null;
            Colors = null;
            Lights = null;
            Indices = null;
        }
    }
}

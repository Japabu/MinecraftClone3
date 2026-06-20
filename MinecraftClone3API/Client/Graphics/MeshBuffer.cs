using System.Collections.Generic;
using OpenTK.Mathematics;

namespace MinecraftClone3API.Graphics
{
    /// <summary>
    /// CPU-side vertex/index accumulator a chunk mesh is built into (by <see cref="MinecraftClone3API.Util.
    /// ChunkMesher"/> on the mesh thread). Holds no GL state, so building it is safe off the render thread.
    /// The opaque path uploads these lists into the shared <see cref="ChunkMeshArena"/>; the transparent path
    /// keeps a per-chunk <see cref="VertexArrayObject"/> (which derives from this and adds the GL buffers).
    /// Backing lists are rented from <see cref="VaoBufferPool"/> so a remesh allocates nothing steady-state.
    /// </summary>
    public class MeshBuffer
    {
        protected static readonly uint[] FaceIndices = {2, 1, 0, 2, 3, 1};
        protected static readonly uint[] FlippedFaceIndices = {0, 2, 3, 0, 3, 1};

        public List<Vector3> Positions;
        public List<Vector4> TexCoords;
        public List<Vector4> Normals;
        public List<Vector3> Colors;
        // xyz = baked block-light brightness (0..1 per channel), w = baked sky-light brightness (0..1).
        // The composition shader multiplies w by the sun colour for the dynamic day/night cycle.
        public List<Vector4> Lights;
        public List<uint> Indices;

        public int VertexCount => (Positions?.Count).GetValueOrDefault();
        public int IndicesCount => (Indices?.Count).GetValueOrDefault();

        public virtual void Add(Vector3 position, Vector4 texCoord, Vector4 normal, Vector3 color, Vector4 light)
        {
            if (Positions == null)
            {
                Positions = VaoBufferPool.RentVector3();
                TexCoords = VaoBufferPool.RentVector4();
                Normals = VaoBufferPool.RentVector4();
                Colors = VaoBufferPool.RentVector3();
                Lights = VaoBufferPool.RentVector4();

                Indices = VaoBufferPool.RentUint();
            }

            Positions.Add(position);
            TexCoords.Add(texCoord);
            Normals.Add(normal);
            Colors.Add(color);
            Lights.Add(light);
        }

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
            VaoBufferPool.Return(TexCoords);
            VaoBufferPool.Return(Normals);
            VaoBufferPool.Return(Colors);
            VaoBufferPool.Return(Lights);
            VaoBufferPool.Return(Indices);

            Positions = null;
            TexCoords = null;
            Normals = null;
            Colors = null;
            Lights = null;
            Indices = null;
        }
    }
}

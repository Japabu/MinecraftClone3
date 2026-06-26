using MinecraftClone3API.Util;
using Silk.NET.Maths;

namespace MinecraftClone3API.Blocks
{
    public enum BlockFace
    {
        None,
        Left,
        Right,
        Bottom,
        Top,
        Back,
        Front,

        //Aliases for serialization
        West = Left,
        East = Right,
        Down = Bottom,
        Up = Top,
        North = Back,
        South = Front
    }

    public static class BlockFaceHelper
    {
        public static readonly BlockFace[] Faces =
        {
            BlockFace.Left, BlockFace.Right, BlockFace.Bottom,
            BlockFace.Top, BlockFace.Back, BlockFace.Front
        };

        private static readonly Vector3D<int>[] Normals =
        {
            new Vector3D<int>(-1, 0, 0), new Vector3D<int>(+1, 0, 0), new Vector3D<int>(0, -1, 0),
            new Vector3D<int>(0, +1, 0), new Vector3D<int>(0, 0, -1), new Vector3D<int>(0, 0, +1)
        };

        private static readonly BlockFace[] Opposites =
        {
            BlockFace.Right, BlockFace.Left, BlockFace.Top,
            BlockFace.Bottom, BlockFace.Front, BlockFace.Back
        };

        public static Vector3D<float> GetNormal(this BlockFace face) => face.GetNormali().ToVector3();
        public static Vector3D<int> GetNormali(this BlockFace face) => Normals[(int) face-1];
        public static BlockFace GetOpposite(this BlockFace face) => Opposites[(int) face-1];
    }
}
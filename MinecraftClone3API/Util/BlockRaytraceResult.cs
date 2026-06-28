using MinecraftClone3API.Blocks;
using Silk.NET.Maths;

namespace MinecraftClone3API.Util
{
    public class BlockRaytraceResult
    {
        public readonly Block Block;
        public readonly BlockFace Face;
        public readonly Vector3D<int> BlockPos;
        public readonly float Distance;
        public readonly Vector3D<float> Point;
        public readonly AxisAlignedBoundingBox BoundingBox;

        public BlockRaytraceResult(Block block, BlockFace face, Vector3D<int> blockPos, float distance, Vector3D<float> point, AxisAlignedBoundingBox boundingBox)
        {
            Block = block;
            Face = face;
            BlockPos = blockPos;
            Distance = distance;
            Point = point;
            BoundingBox = boundingBox;
        }

        public override string ToString()
        {
            return
                $"BlockRaytraceResult (Face:{Face}; BlockPos:{BlockPos}; Distance:{Distance}; Point:{Point})";
        }
    }
}
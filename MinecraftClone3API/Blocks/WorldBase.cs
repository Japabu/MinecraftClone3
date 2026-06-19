using MinecraftClone3API.Entities;
using MinecraftClone3API.Util;
using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace MinecraftClone3API.Blocks
{
    public abstract class WorldBase
    {
        // Concurrent: the main thread mutates it (Update/SetBlock) and renders from it while the
        // load/unload threads read it. A plain Dictionary corrupts under that concurrent access.
        public readonly ConcurrentDictionary<Vector3i, Chunk> LoadedChunks = new ConcurrentDictionary<Vector3i, Chunk>();

        public static Vector3i ChunkInWorld(Vector3i v) => ChunkInWorld(v.X, v.Y, v.Z);

        public static Vector3i ChunkInWorld(int x, int y, int z) => new Vector3i(
            x < 0 ? (x + 1) / Chunk.Size - 1 : x / Chunk.Size,
            y < 0 ? (y + 1) / Chunk.Size - 1 : y / Chunk.Size,
            z < 0 ? (z + 1) / Chunk.Size - 1 : z / Chunk.Size);

        public static Vector3i BlockInChunk(int x, int y, int z) => new Vector3i(
            x < 0 ? (x + 1) % Chunk.Size + Chunk.Size - 1 : x % Chunk.Size,
            y < 0 ? (y + 1) % Chunk.Size + Chunk.Size - 1 : y % Chunk.Size,
            z < 0 ? (z + 1) % Chunk.Size + Chunk.Size - 1 : z % Chunk.Size);

        public void SetBlock(Vector3i blockPos, Block block) => SetBlock(blockPos.X, blockPos.Y, blockPos.Z, block);
        public void SetBlock(int x, int y, int z, Block block) => SetBlock(x, y, z, block, true, false);
        public Block GetBlock(Vector3i blockPos) => GetBlock(blockPos.X, blockPos.Y, blockPos.Z);
        public void SetBlockData(Vector3i blockPos, BlockData data) => SetBlockData(blockPos.X, blockPos.Y, blockPos.Z, data);
        public BlockData GetBlockData(Vector3i blockPos) => GetBlockData(blockPos.X, blockPos.Y, blockPos.Z);
        public void SetBlockLightLevel(Vector3i blockPos, LightLevel lightLevel)
            => SetBlockLightLevel(blockPos.X, blockPos.Y, blockPos.Z, lightLevel);
        public LightLevel GetBlockLightLevel(Vector3i blockPos) => GetBlockLightLevel(blockPos.X, blockPos.Y, blockPos.Z);
        public void SetSkyLight(Vector3i blockPos, int level) => SetSkyLight(blockPos.X, blockPos.Y, blockPos.Z, level);
        public int GetSkyLight(Vector3i blockPos) => GetSkyLight(blockPos.X, blockPos.Y, blockPos.Z);

        public void SetBlockLightLevelColor(Vector3i blockPos, int value, int color)
        {
            var lightLevel = GetBlockLightLevel(blockPos);
            lightLevel[color] = value;
            SetBlockLightLevel(blockPos, lightLevel);
        }
        public int GetBlockLightLevelColor(Vector3i blockPos, int color) => GetBlockLightLevel(blockPos)[color];

        public BlockRaytraceResult BlockRaytrace(Vector3 position, Vector3 direction, float range)
        {
            const float epsilon = -1e-6f;

            direction.Normalize();
            var start = (position - direction * 0.5f).ToVector3i();
            var end = (position + direction * (range + 0.5f)).ToVector3i();

            var minX = Math.Min(start.X, end.X) - 1;
            var minY = Math.Min(start.Y, end.Y) - 1;
            var minZ = Math.Min(start.Z, end.Z) - 1;

            var maxX = Math.Max(start.X, end.X) + 1;
            var maxY = Math.Max(start.Y, end.Y) + 1;
            var maxZ = Math.Max(start.Z, end.Z) + 1;

            BlockRaytraceResult result = null;
            for (var x = minX; x <= maxX; x++)
                for (var y = minY; y <= maxY; y++)
                    for (var z = minZ; z <= maxZ; z++)
                    {
                        var block = GetBlock(x, y, z);
                        var blockPosi = new Vector3i(x, y, z);
                        var bb = block.GetBoundingBox(this, blockPosi);
                        if (bb == null || !block.CanTarget(this, blockPosi)) continue;

                        var translation = bb.Min + (bb.Max - bb.Min) * 0.5f;
                        var scale = bb.Max - bb.Min;

                        foreach (var face in BlockFaceHelper.Faces)
                        {
                            var normal = face.GetNormal();
                            var divisor = Vector3.Dot(normal, direction);

                            //ignore back faces
                            if (divisor >= epsilon) continue;

                            var planeNormal = normal * normal;
                            var blockPos = new Vector3(x, y, z) + translation;
                            var blockSize = new Vector3(0.5f) * scale;
                            var d = -(Vector3.Dot(blockPos, planeNormal) + Vector3.Dot(blockSize, normal));
                            var numerator = Vector3.Dot(planeNormal, position) + d;
                            var distance = Math.Abs(-numerator / divisor);

                            var point = position + distance * direction;
                            if (point.X < x + translation.X - blockSize.X + epsilon ||
                                point.X > x + translation.X + blockSize.X - epsilon ||
                                point.Y < y + translation.Y - blockSize.Y + epsilon ||
                                point.Y > y + translation.Y + blockSize.Y - epsilon ||
                                point.Z < z + translation.Z - blockSize.Z + epsilon ||
                                point.Z > z + translation.Z + blockSize.Z - epsilon) continue;

                            if (distance <= range && (result == null || result.Distance > distance))
                                result = new BlockRaytraceResult(block, face, new Vector3i(x, y, z), distance,
                                    point, bb);
                        }
                    }

            return result;
        }

        public bool IsBlockInEmptyChunk(Vector3i blockPos) => !LoadedChunks.ContainsKey(ChunkInWorld(blockPos));
        public bool IsOpaqueFullBlock(Vector3i blockPos) => GetBlock(blockPos).IsOpaqueFullBlock(this, blockPos);
        public bool IsFullBlock(Vector3i blockPos) => GetBlock(blockPos).IsFullBlock(this, blockPos);

        public abstract void SetBlock(int x, int y, int z, Block block, bool update, bool lowPriority);
        public abstract Block GetBlock(int x, int y, int z);
        public abstract void SetBlockData(int x, int y, int z, BlockData data);
        public abstract BlockData GetBlockData(int x, int y, int z);
        public abstract void SetBlockLightLevel(int x, int y, int z, LightLevel lightLevel);
        public abstract LightLevel GetBlockLightLevel(int x, int y, int z);
        public abstract void SetSkyLight(int x, int y, int z, int level);
        public abstract int GetSkyLight(int x, int y, int z);
        public abstract void PlaceBlock(EntityPlayer player, Vector3i blockPos, Block block);
        public abstract void Update();

    }
}

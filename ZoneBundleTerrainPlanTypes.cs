using System;
using System.Collections.Generic;
using UnityEngine;
using static ZoneSavior.ZoneBundleTerrainGrid;

namespace ZoneSavior;

internal static partial class ZoneBundleTerrain
{
    private sealed class TerrainSupportApplicationPlan
    {
        public TerrainSupportApplicationPlan(Dictionary<long, float> supportHeights, List<TerrainSupportCell> supportCells)
        {
            SupportHeights = supportHeights;
            SupportCells = supportCells;
        }

        public Dictionary<long, float> SupportHeights { get; }
        public List<TerrainSupportCell> SupportCells { get; }
        public bool HasSupport => SupportHeights.Count > 0;
    }

    private readonly struct WorldSupportContactPlan
    {
        public WorldSupportContactPlan(Dictionary<long, float> supportHeights, List<TerrainSupportCell> supportCells, List<Vector2i> zones)
        {
            SupportHeights = supportHeights;
            SupportCells = supportCells;
            Zones = zones;
        }

        public Dictionary<long, float> SupportHeights { get; }
        public List<TerrainSupportCell> SupportCells { get; }
        public List<Vector2i> Zones { get; }
        public bool HasSupport => SupportCells.Count > 0;
    }

    private readonly struct TerrainSupportCell
    {
        public TerrainSupportCell(int x, int z, float height)
        {
            X = x;
            Z = z;
            Height = height;
        }

        public int X { get; }
        public int Z { get; }
        public float Height { get; }
    }

    private sealed class TerrainSupportCellIndex
    {
        private readonly Dictionary<long, List<TerrainSupportCell>> _cellsByBucket = [];
        private readonly float _bucketSize;
        private readonly int _searchRadius;

        public TerrainSupportCellIndex(IEnumerable<TerrainSupportCell> cells, float featherWidth)
        {
            _bucketSize = Mathf.Max(1f, featherWidth);
            _searchRadius = Mathf.Max(0, Mathf.CeilToInt(featherWidth / _bucketSize));
            foreach (TerrainSupportCell cell in cells)
            {
                long key = PackCell(ToBucket(cell.X), ToBucket(cell.Z));
                if (!_cellsByBucket.TryGetValue(key, out List<TerrainSupportCell> bucket))
                {
                    bucket = [];
                    _cellsByBucket[key] = bucket;
                }

                bucket.Add(cell);
            }
        }

        public bool TryGetNearest(Vector3 node, float maxDistanceSqr, out TerrainSupportCell nearest, out float bestDistanceSqr)
        {
            nearest = default;
            bestDistanceSqr = float.PositiveInfinity;
            if (_cellsByBucket.Count == 0)
            {
                return false;
            }

            int bucketX = ToBucket(node.x);
            int bucketZ = ToBucket(node.z);
            for (int z = bucketZ - _searchRadius; z <= bucketZ + _searchRadius; z++)
            {
                for (int x = bucketX - _searchRadius; x <= bucketX + _searchRadius; x++)
                {
                    if (!_cellsByBucket.TryGetValue(PackCell(x, z), out List<TerrainSupportCell> bucket))
                    {
                        continue;
                    }

                    foreach (TerrainSupportCell cell in bucket)
                    {
                        float dx = node.x - cell.X;
                        float dz = node.z - cell.Z;
                        float distanceSqr = dx * dx + dz * dz;
                        if (distanceSqr >= bestDistanceSqr || distanceSqr > maxDistanceSqr)
                        {
                            continue;
                        }

                        bestDistanceSqr = distanceSqr;
                        nearest = cell;
                    }
                }
            }

            return !float.IsPositiveInfinity(bestDistanceSqr);
        }

        private int ToBucket(float value)
        {
            return Mathf.FloorToInt(value / _bucketSize);
        }
    }

    internal readonly struct TerrainSourceAnchor
    {
        public TerrainSourceAnchor(float baseWorldY)
        {
            BaseWorldY = baseWorldY;
        }

        public float BaseWorldY { get; }
    }
}

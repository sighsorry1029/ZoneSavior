using UnityEngine;

namespace ZoneSavior;

internal static class ZoneBundleTerrainGrid
{
    public static long PackCell(int x, int z)
    {
        return ((long)x << 32) ^ (uint)z;
    }

    public static void UnpackCell(long key, out int x, out int z)
    {
        x = (int)(key >> 32);
        z = (int)key;
    }

    public static Vector3 VertexToWorld(Heightmap heightmap, int x, int z)
    {
        Vector3 position = heightmap.transform.position;
        position.x += (x - heightmap.m_width / 2) * heightmap.m_scale;
        position.z += (z - heightmap.m_width / 2) * heightmap.m_scale;
        return position;
    }
}

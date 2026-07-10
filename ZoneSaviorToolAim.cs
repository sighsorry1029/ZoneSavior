using UnityEngine;

namespace ZoneSavior;

internal static class ZoneToolAim
{
    public static float SampleGroundY(float x, float z, float fallbackY)
    {
        if (ZoneSystem.instance == null)
        {
            return fallbackY;
        }

        Vector3 point = new(x, fallbackY, z);
        ZoneSystem.instance.GetGroundData(ref point, out _, out _, out _, out _);
        return point.y;
    }
}

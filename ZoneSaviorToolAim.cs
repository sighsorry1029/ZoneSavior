using System;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneToolAim
{
    private const float NativePlacementRayDistance = 50f;

    public static bool TryGetAimPoint(Player player, float maxToolDistance, out Vector3 point)
    {
        point = default;
        if (player == null)
        {
            return false;
        }

        if (TryGetCameraRay(out Vector3 origin, out Vector3 direction))
        {
            int mask = player.m_placeRayMask != 0 ? player.m_placeRayMask : Physics.DefaultRaycastLayers;
            float rayDistance = Mathf.Max(NativePlacementRayDistance, maxToolDistance);
            if (TryRaycastAim(player, origin, direction, rayDistance, mask, ignoreRoot: null, out point))
            {
                point.y = SampleGroundY(point.x, point.z, point.y);
                return true;
            }
        }

        return false;
    }

    public static bool TryGetRawAimPoint(Player player, float maxToolDistance, GameObject? ignoreRoot, out Vector3 point)
    {
        point = default;
        if (player == null || !TryGetCameraRay(out Vector3 origin, out Vector3 direction))
        {
            return false;
        }

        float rayDistance = Mathf.Max(NativePlacementRayDistance, maxToolDistance);
        int primaryMask = player.m_placeRayMask != 0 ? player.m_placeRayMask : Physics.DefaultRaycastLayers;
        if (TryRaycastAim(player, origin, direction, rayDistance, primaryMask, ignoreRoot, out point))
        {
            return true;
        }

        return primaryMask != Physics.DefaultRaycastLayers &&
               TryRaycastAim(player, origin, direction, rayDistance, Physics.DefaultRaycastLayers, ignoreRoot, out point);
    }

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

    private static bool TryGetCameraRay(out Vector3 origin, out Vector3 direction)
    {
        if (GameCamera.instance != null)
        {
            Transform cameraTransform = GameCamera.instance.transform;
            origin = cameraTransform.position;
            direction = cameraTransform.forward;
            return true;
        }

        Camera camera = Utils.GetMainCamera();
        if (camera != null)
        {
            Ray ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
            origin = ray.origin;
            direction = ray.direction;
            return true;
        }

        origin = default;
        direction = default;
        return false;
    }

    private static bool TryRaycastAim(
        Player player,
        Vector3 origin,
        Vector3 direction,
        float rayDistance,
        int mask,
        GameObject? ignoreRoot,
        out Vector3 point)
    {
        point = default;
        RaycastHit[] hits = Physics.RaycastAll(origin, direction, rayDistance, mask, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));

        foreach (RaycastHit hit in hits)
        {
            Collider collider = hit.collider;
            if (!collider)
            {
                continue;
            }

            Transform hitTransform = collider.transform;
            if ((ignoreRoot && hitTransform.IsChildOf(ignoreRoot.transform)) ||
                (player && hitTransform.IsChildOf(player.transform)))
            {
                continue;
            }

            point = hit.point;
            return true;
        }

        return false;
    }
}

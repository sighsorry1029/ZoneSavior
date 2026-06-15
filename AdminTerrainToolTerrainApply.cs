using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    private static void ApplyStoredSettings(ZoneSaviorTerrainProxy proxy, bool force, bool notify = false)
    {
        ZNetView nview = proxy.GetComponent<ZNetView>();
        ZDO zdo = nview ? nview.GetZDO() : null!;
        if (zdo == null || (!force && zdo.GetBool(AppliedHash, false)))
        {
            return;
        }

        TerrainProxySettings settings = ReadSettings(zdo);
        if (settings.ModifiesHeight)
        {
            ZoneSaviorTerrainMistileCompat.RegisterIgnoredTerrainArea(
                proxy.transform.position,
                settings.SearchRadius,
                "ZoneSavior terrain proxy");
        }

        int changed = ApplyTerrain(proxy.transform.position, proxy.transform.rotation, settings);
        zdo.Set(AppliedHash, true);
        zdo.Set(AppliedPositionHash, proxy.transform.position);
        string label = settings.Mode == TerrainProxyMode.Paint ? "paint proxy" : "terrain proxy";
        string details = settings.Mode == TerrainProxyMode.Paint
            ? $"Paint={settings.PaintType}, Radius={settings.SearchRadius:0.##}"
            : $"TargetY={proxy.transform.position.y:0.##}, Radius={settings.SearchRadius:0.##}";

        if (changed > 0)
        {
            ReportTerrainResult(
                $"ZoneSavior {label} applied {changed} terrain compiler(s). Mode={settings.Mode}, {details}.",
                notify);
        }
        else
        {
            ReportTerrainResult(
                $"ZoneSavior {label} made no terrain changes. Mode={settings.Mode}, {details}.",
                notify);
        }
    }

    private static int ApplyTerrain(Vector3 position, Quaternion rotation, TerrainProxySettings settings)
    {
        List<Heightmap> heightmaps = [];
        Heightmap.FindHeightmap(position, settings.SearchRadius + SearchPadding, heightmaps);

        int changed = 0;
        HashSet<TerrainComp> seen = [];
        foreach (Heightmap heightmap in heightmaps)
        {
            if (!heightmap)
            {
                continue;
            }

            TerrainComp compiler = heightmap.GetAndCreateTerrainCompiler();
            if (!compiler || !seen.Add(compiler))
            {
                continue;
            }

            if (ApplyTerrainCompiler(compiler, position, rotation, settings))
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            ClutterSystem.instance?.ResetGrass(position, settings.SearchRadius + SearchPadding);
        }

        return changed;
    }

    private static int ResetTerrain(Vector3 position, Quaternion rotation, TerrainProxySettings settings)
    {
        List<Heightmap> heightmaps = [];
        Heightmap.FindHeightmap(position, settings.SearchRadius + SearchPadding, heightmaps);

        int changed = 0;
        HashSet<TerrainComp> seen = [];
        foreach (Heightmap heightmap in heightmaps)
        {
            if (!heightmap)
            {
                continue;
            }

            TerrainComp compiler = TerrainComp.FindTerrainCompiler(heightmap.transform.position);
            if (!compiler || !seen.Add(compiler))
            {
                continue;
            }

            if (ResetTerrainCompiler(compiler, position, rotation, settings))
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            ClutterSystem.instance?.ResetGrass(position, settings.SearchRadius + SearchPadding);
        }

        return changed;
    }

    private static int ResetIntersectingProxyObjects(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        ZNetView? placedView,
        out int terrainCompilers)
    {
        terrainCompilers = 0;
        ZNetScene scene = ZNetScene.instance;
        if (scene == null)
        {
            return 0;
        }

        List<ZNetView> views = scene.m_instances.Values.ToList();
        int removed = 0;
        foreach (ZNetView view in views)
        {
            if (!view || view == placedView || !IsProxyObject(view.gameObject))
            {
                continue;
            }

            ZDO zdo = view.GetZDO();
            if (zdo == null || !HasStoredSettings(zdo))
            {
                continue;
            }

            TerrainProxySettings proxySettings = ReadSettings(zdo);
            Vector3 proxyPosition = view.transform.position;
            Quaternion proxyRotation = view.transform.rotation;
            if (!FootprintsIntersect(position, rotation, settings, proxyPosition, proxyRotation, proxySettings))
            {
                continue;
            }

            terrainCompilers += ResetTerrain(proxyPosition, proxyRotation, proxySettings);
            DestroyProxy(view);
            removed++;
        }

        return removed;
    }

    private static int ResetIntersectingPaintProxyObjects(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        ZNetView? placedView,
        out int terrainCompilers)
    {
        terrainCompilers = 0;
        ZNetScene scene = ZNetScene.instance;
        if (scene == null)
        {
            return 0;
        }

        List<ZNetView> views = scene.m_instances.Values.ToList();
        int removed = 0;
        foreach (ZNetView view in views)
        {
            if (!view || view == placedView || !IsPaintProxyObject(view.gameObject))
            {
                continue;
            }

            ZDO zdo = view.GetZDO();
            if (zdo == null || !HasStoredSettings(zdo))
            {
                continue;
            }

            TerrainProxySettings proxySettings = ReadSettings(zdo);
            Vector3 proxyPosition = view.transform.position;
            Quaternion proxyRotation = view.transform.rotation;
            if (!FootprintsIntersect(position, rotation, settings, proxyPosition, proxyRotation, proxySettings))
            {
                continue;
            }

            terrainCompilers += ResetTerrain(proxyPosition, proxyRotation, proxySettings);
            DestroyProxy(view);
            removed++;
        }

        return removed;
    }

    private static bool FootprintsIntersect(
        Vector3 aPosition,
        Quaternion aRotation,
        TerrainProxySettings aSettings,
        Vector3 bPosition,
        Quaternion bRotation,
        TerrainProxySettings bSettings)
    {
        bool aCircle = aSettings.HasCircleFootprint;
        bool bCircle = bSettings.HasCircleFootprint;
        if (aCircle && bCircle)
        {
            return Vector2.Distance(ToXZ(aPosition), ToXZ(bPosition)) <= aSettings.Radius + bSettings.Radius;
        }

        if (aCircle)
        {
            return CircleIntersectsRect(aPosition, aSettings.Radius, bPosition, bRotation, bSettings.Width, bSettings.Length);
        }

        if (bCircle)
        {
            return CircleIntersectsRect(bPosition, bSettings.Radius, aPosition, aRotation, aSettings.Width, aSettings.Length);
        }

        return RectIntersectsRect(
            aPosition,
            aRotation,
            aSettings.Width,
            aSettings.Length,
            bPosition,
            bRotation,
            bSettings.Width,
            bSettings.Length);
    }

    private static bool CircleIntersectsRect(
        Vector3 circlePosition,
        float radius,
        Vector3 rectPosition,
        Quaternion rectRotation,
        float rectWidth,
        float rectLength)
    {
        Vector3 local = InverseYaw(rectRotation) * (circlePosition - rectPosition);
        float x = Mathf.Clamp(local.x, rectWidth * -0.5f, rectWidth * 0.5f);
        float z = Mathf.Clamp(local.z, rectLength * -0.5f, rectLength * 0.5f);
        float dx = local.x - x;
        float dz = local.z - z;
        return dx * dx + dz * dz <= radius * radius;
    }

    private static bool RectIntersectsRect(
        Vector3 aPosition,
        Quaternion aRotation,
        float aWidth,
        float aLength,
        Vector3 bPosition,
        Quaternion bRotation,
        float bWidth,
        float bLength)
    {
        Vector2 aCenter = ToXZ(aPosition);
        Vector2 bCenter = ToXZ(bPosition);
        GetRectAxes(aRotation, out Vector2 aX, out Vector2 aZ);
        GetRectAxes(bRotation, out Vector2 bX, out Vector2 bZ);
        Vector2 delta = bCenter - aCenter;

        return OverlapsOnAxis(aX, delta, aX, aZ, aWidth * 0.5f, aLength * 0.5f, bX, bZ, bWidth * 0.5f, bLength * 0.5f) &&
               OverlapsOnAxis(aZ, delta, aX, aZ, aWidth * 0.5f, aLength * 0.5f, bX, bZ, bWidth * 0.5f, bLength * 0.5f) &&
               OverlapsOnAxis(bX, delta, aX, aZ, aWidth * 0.5f, aLength * 0.5f, bX, bZ, bWidth * 0.5f, bLength * 0.5f) &&
               OverlapsOnAxis(bZ, delta, aX, aZ, aWidth * 0.5f, aLength * 0.5f, bX, bZ, bWidth * 0.5f, bLength * 0.5f);
    }

    private static bool OverlapsOnAxis(
        Vector2 axis,
        Vector2 centerDelta,
        Vector2 aX,
        Vector2 aZ,
        float aHalfWidth,
        float aHalfLength,
        Vector2 bX,
        Vector2 bZ,
        float bHalfWidth,
        float bHalfLength)
    {
        float distance = Mathf.Abs(Vector2.Dot(centerDelta, axis));
        float aProjection = aHalfWidth * Mathf.Abs(Vector2.Dot(aX, axis)) + aHalfLength * Mathf.Abs(Vector2.Dot(aZ, axis));
        float bProjection = bHalfWidth * Mathf.Abs(Vector2.Dot(bX, axis)) + bHalfLength * Mathf.Abs(Vector2.Dot(bZ, axis));
        return distance <= aProjection + bProjection;
    }

    private static void GetRectAxes(Quaternion rotation, out Vector2 xAxis, out Vector2 zAxis)
    {
        Quaternion yaw = Yaw(rotation);
        Vector3 right = yaw * Vector3.right;
        Vector3 forward = yaw * Vector3.forward;
        xAxis = ToXZ(right).normalized;
        zAxis = ToXZ(forward).normalized;
    }

    private static Quaternion InverseYaw(Quaternion rotation)
    {
        return Quaternion.Inverse(Yaw(rotation));
    }

    private static Quaternion Yaw(Quaternion rotation)
    {
        return Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
    }

    private static Vector2 ToXZ(Vector3 value)
    {
        return new Vector2(value.x, value.z);
    }

    private static bool ApplyTerrainCompiler(TerrainComp compiler, Vector3 position, Quaternion rotation, TerrainProxySettings settings)
    {
        if (!compiler.m_nview || !compiler.m_nview.IsValid())
        {
            return false;
        }

        if (!compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        Heightmap heightmap = compiler.m_hmap;
        int width = heightmap.m_width + 1;
        int count = width * width;
        if (compiler.m_modifiedHeight.Length != count ||
            compiler.m_levelDelta.Length != count ||
            compiler.m_smoothDelta.Length != count ||
            compiler.m_modifiedPaint.Length != count ||
            compiler.m_paintMask.Length != count)
        {
            return false;
        }

        bool changed = false;
        Quaternion inverseYaw = Quaternion.Inverse(Quaternion.Euler(0f, rotation.eulerAngles.y, 0f));
        if (!TryGetHeightmapIndexRange(heightmap, position, settings.SearchRadius + SearchPadding, out int minX, out int maxX, out int minZ, out int maxZ))
        {
            return false;
        }

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int index = z * width + x;
                Vector3 node = VertexToWorld(heightmap, x, z);
                Vector3 local = inverseYaw * (node - position);
                float terrainNormalized = settings.GetNormalizedDistance(local.x, local.z);

                if (settings.ModifiesHeight && terrainNormalized <= 1f)
                {
                    float targetHeight = settings.GetTargetHeight(position, heightmap.transform.position.y, local.z);
                    float height = heightmap.GetHeight(x, z);
                    changed |= ApplyHeightNode(compiler, index, height, targetHeight, local.x, local.z, settings);
                }

                if (settings.ModifiesPaint)
                {
                    changed |= ApplyPaintNode(compiler, heightmap, x, z, index, terrainNormalized, settings);
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        compiler.m_operations++;
        compiler.m_lastOpPoint = position;
        compiler.m_lastOpRadius = settings.SearchRadius;
        compiler.Save();
        heightmap.Poke(delayed: false);
        return true;
    }

    private static bool ResetTerrainCompiler(TerrainComp compiler, Vector3 position, Quaternion rotation, TerrainProxySettings settings)
    {
        if (!compiler.m_nview || !compiler.m_nview.IsValid())
        {
            return false;
        }

        if (!compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        Heightmap heightmap = compiler.m_hmap;
        int width = heightmap.m_width + 1;
        int count = width * width;
        if (compiler.m_modifiedHeight.Length != count ||
            compiler.m_levelDelta.Length != count ||
            compiler.m_smoothDelta.Length != count ||
            compiler.m_modifiedPaint.Length != count ||
            compiler.m_paintMask.Length != count)
        {
            return false;
        }

        bool changed = false;
        Quaternion inverseYaw = Quaternion.Inverse(Quaternion.Euler(0f, rotation.eulerAngles.y, 0f));
        if (!TryGetHeightmapIndexRange(heightmap, position, settings.SearchRadius + SearchPadding, out int minX, out int maxX, out int minZ, out int maxZ))
        {
            return false;
        }

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int index = z * width + x;
                Vector3 node = VertexToWorld(heightmap, x, z);
                Vector3 local = inverseYaw * (node - position);
                if (settings.GetNormalizedDistance(local.x, local.z) > 1f)
                {
                    continue;
                }

                if (settings.ModifiesHeight &&
                    (compiler.m_modifiedHeight[index] ||
                     Mathf.Abs(compiler.m_levelDelta[index]) > 0.001f ||
                     Mathf.Abs(compiler.m_smoothDelta[index]) > 0.001f))
                {
                    compiler.m_modifiedHeight[index] = false;
                    compiler.m_levelDelta[index] = 0f;
                    compiler.m_smoothDelta[index] = 0f;
                    changed = true;
                }

                if (compiler.m_modifiedPaint[index])
                {
                    compiler.m_modifiedPaint[index] = false;
                    compiler.m_paintMask[index] = Color.clear;
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        compiler.m_operations++;
        compiler.m_lastOpPoint = position;
        compiler.m_lastOpRadius = settings.SearchRadius;
        compiler.Save();
        heightmap.Poke(delayed: false);
        return true;
    }

    private static bool TryGetHeightmapIndexRange(
        Heightmap heightmap,
        Vector3 position,
        float radius,
        out int minX,
        out int maxX,
        out int minZ,
        out int maxZ)
    {
        int maxIndex = heightmap.m_width;
        float scale = Mathf.Max(heightmap.m_scale, 0.001f);
        float halfWidth = heightmap.m_width * 0.5f;
        Vector3 origin = heightmap.transform.position;

        minX = Mathf.Clamp(Mathf.FloorToInt((position.x - radius - origin.x) / scale + halfWidth) - 1, 0, maxIndex);
        maxX = Mathf.Clamp(Mathf.CeilToInt((position.x + radius - origin.x) / scale + halfWidth) + 1, 0, maxIndex);
        minZ = Mathf.Clamp(Mathf.FloorToInt((position.z - radius - origin.z) / scale + halfWidth) - 1, 0, maxIndex);
        maxZ = Mathf.Clamp(Mathf.CeilToInt((position.z + radius - origin.z) / scale + halfWidth) + 1, 0, maxIndex);

        return minX <= maxX && minZ <= maxZ;
    }

    private static bool ApplyHeightNode(
        TerrainComp compiler,
        int index,
        float currentHeight,
        float targetHeight,
        float localX,
        float localZ,
        TerrainProxySettings settings)
    {
        float delta = (targetHeight - currentHeight + compiler.m_smoothDelta[index]) * settings.GetLevelFalloff(localX, localZ);
        compiler.m_smoothDelta[index] = 0f;

        float previous = compiler.m_levelDelta[index];
        compiler.m_levelDelta[index] = Mathf.Clamp(previous + delta, -AdminTerrainMaxDelta, AdminTerrainMaxDelta);
        compiler.m_modifiedHeight[index] = Mathf.Abs(compiler.m_levelDelta[index]) > 0.001f;
        return Mathf.Abs(previous - compiler.m_levelDelta[index]) > 0.001f;
    }

    private static bool ApplyPaintNode(
        TerrainComp compiler,
        Heightmap heightmap,
        int x,
        int z,
        int index,
        float normalized,
        TerrainProxySettings settings)
    {
        if (normalized > 1f)
        {
            return false;
        }

        Color current = heightmap.GetPaintMask(x, z);
        Color target = GetPaintTarget(settings.PaintType, current);
        Color desired = Color.Lerp(current, target, Mathf.Pow(1f - Mathf.Clamp01(normalized), 0.1f));
        if (settings.PaintType != AdminTerrainPaintType.ClearVegetation)
        {
            desired.a = current.a;
        }

        if (compiler.m_modifiedPaint[index])
        {
            return !Approximately(compiler.m_paintMask[index], desired) && SetPaintNode(compiler, index, desired);
        }

        if (Approximately(current, desired))
        {
            return false;
        }

        return SetPaintNode(compiler, index, desired);
    }

    private static bool SetPaintNode(TerrainComp compiler, int index, Color desired)
    {
        compiler.m_modifiedPaint[index] = true;
        compiler.m_paintMask[index] = desired;
        return true;
    }

    private static Color GetPaintTarget(AdminTerrainPaintType paintType, Color current)
    {
        return paintType switch
        {
            AdminTerrainPaintType.Grass => Heightmap.m_paintMaskNothing,
            AdminTerrainPaintType.Dirt => Heightmap.m_paintMaskDirt,
            AdminTerrainPaintType.Cultivated => Heightmap.m_paintMaskCultivated,
            AdminTerrainPaintType.Paved => Heightmap.m_paintMaskPaved,
            AdminTerrainPaintType.DarkGrass => new Color(0.6f, 0.5f, 0f),
            AdminTerrainPaintType.PatchyGrass => new Color(0f, 0.75f, 0f),
            AdminTerrainPaintType.MossyPaving => new Color(0f, 0f, 0.5f),
            AdminTerrainPaintType.DirtPaving => new Color(1f, 0f, 0.5f),
            AdminTerrainPaintType.DarkPaving => new Color(0f, 1f, 0.5f),
            AdminTerrainPaintType.ClearVegetation => Heightmap.m_paintMaskClearVegetation,
            _ => current
        };
    }

    private static Vector3 VertexToWorld(Heightmap heightmap, int x, int z)
    {
        Vector3 position = heightmap.transform.position;
        position.x += (x - heightmap.m_width / 2f) * heightmap.m_scale;
        position.z += (z - heightmap.m_width / 2f) * heightmap.m_scale;
        return position;
    }

    private static bool Approximately(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.001f &&
               Mathf.Abs(a.g - b.g) < 0.001f &&
               Mathf.Abs(a.b - b.b) < 0.001f &&
               Mathf.Abs(a.a - b.a) < 0.001f;
    }
}

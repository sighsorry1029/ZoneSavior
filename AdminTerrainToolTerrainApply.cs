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

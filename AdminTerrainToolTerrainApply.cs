using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    private const float MaxPersistentProxyFootprintRadius = MaxSlopeSize * 0.7071068f;

    private static bool ApplyStoredSettings(ZoneSaviorTerrainProxy proxy, bool force, bool notify = false)
    {
        ZNetView nview = proxy.GetComponent<ZNetView>();
        ZDO zdo = nview ? nview.GetZDO() : null!;
        if (zdo == null || (!force && zdo.GetBool(AppliedHash, false)))
        {
            return true;
        }

        TerrainProxySettings settings = ReadSettings(zdo);
        int changed = ApplyTerrain(
            proxy,
            proxy.transform.position,
            proxy.transform.rotation,
            settings,
            out bool terrainReady);
        if (!terrainReady)
        {
            return false;
        }

        zdo.Set(AppliedHash, true);
        zdo.Set(AppliedPositionHash, proxy.transform.position);
        zdo.Set(AppliedYawHash, proxy.transform.rotation.eulerAngles.y);
        zdo.Set(AppliedSettingsHash, GetSettingsFingerprint(settings));
        string label = settings.Mode == TerrainProxyMode.Paint ? "paint proxy" : "terrain proxy";
        string details = settings.Mode switch
        {
            TerrainProxyMode.Paint => $"Paint={settings.PaintType}, Radius={settings.SearchRadius:0.##}",
            TerrainProxyMode.Slope =>
                $"CenterY={proxy.transform.position.y:0.##}, Width={settings.Width:0.##}, " +
                $"Length={settings.Length:0.##}, HeightDelta={settings.SlopeHeightDelta:0.##}, " +
                $"Yaw={proxy.transform.rotation.eulerAngles.y:0.##}",
            _ => $"TargetY={proxy.transform.position.y:0.##}, Radius={settings.SearchRadius:0.##}"
        };

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

        return true;
    }

    private static int ApplyTerrain(
        ZoneSaviorTerrainProxy proxy,
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        out bool terrainReady)
    {
        List<Heightmap> heightmaps = [];
        Heightmap.FindHeightmap(position, settings.SearchRadius + SearchPadding, heightmaps);

        List<TerrainComp> preparedCompilers = [];
        List<Heightmap> preparedHeightmaps = [];
        List<int> preparedWidths = [];
        bool loadedTerrainReady = true;
        HashSet<TerrainComp> seen = [];
        foreach (Heightmap heightmap in heightmaps)
        {
            if (!heightmap ||
                heightmap.IsDistantLod ||
                !FootprintIntersectsHeightmap(position, rotation, settings, heightmap))
            {
                continue;
            }

            TerrainComp compiler = heightmap.GetAndCreateTerrainCompiler();
            if (!compiler)
            {
                loadedTerrainReady = false;
                continue;
            }

            if (!seen.Add(compiler))
            {
                loadedTerrainReady = false;
                continue;
            }

            if (!TryInspectTerrainCompiler(compiler, out Heightmap preparedHeightmap, out int width) ||
                !ReferenceEquals(preparedHeightmap, heightmap))
            {
                loadedTerrainReady = false;
                continue;
            }

            preparedCompilers.Add(compiler);
            preparedHeightmaps.Add(preparedHeightmap);
            preparedWidths.Add(width);
        }

        terrainReady = loadedTerrainReady &&
                       IsFootprintCoveredByHeightmaps(
                           position,
                           rotation,
                           settings,
                           heightmaps,
                           preparedHeightmaps);

        // Full-size height footprints can extend beyond the terrain streamed at one time.
        // Hard-edged height application is absolute, so applying each fully prepared streamed batch is safe;
        // keep Applied=false until a single batch proves full coverage.
        bool canApplyPreparedBatch = terrainReady ||
                                     (loadedTerrainReady &&
                                      preparedCompilers.Count > 0 &&
                                      settings.HasIdempotentHeightApplication);
        if (!canApplyPreparedBatch)
        {
            return 0;
        }

        bool partialBatch = !terrainReady;
        ulong partialBatchFingerprint = partialBatch
            ? GetPreparedBatchFingerprint(
                position,
                rotation,
                settings,
                preparedCompilers,
                preparedHeightmaps)
            : 0UL;
        if (partialBatch && proxy.HasAppliedPreparedBatch(partialBatchFingerprint))
        {
            return 0;
        }

        foreach (TerrainComp compiler in preparedCompilers)
        {
            if (!TryClaimTerrainCompilerOwnership(compiler))
            {
                terrainReady = false;
                return 0;
            }
        }

        if (settings.ModifiesHeight)
        {
            ZoneSaviorTerrainMistileCompat.RegisterIgnoredTerrainArea(
                position,
                settings.SearchRadius,
                "ZoneSavior terrain proxy");
        }

        int changed = 0;
        for (int index = 0; index < preparedCompilers.Count; index++)
        {
            if (ApplyTerrainCompiler(
                    preparedCompilers[index],
                    preparedHeightmaps[index],
                    preparedWidths[index],
                    position,
                    rotation,
                    settings))
            {
                changed++;
            }
        }

        if (partialBatch)
        {
            proxy.MarkPreparedBatchApplied(partialBatchFingerprint);
        }

        if (changed > 0)
        {
            ClutterSystem.instance?.ResetGrass(position, settings.SearchRadius + SearchPadding);
        }

        return changed;
    }

    private static ulong GetPreparedBatchFingerprint(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        List<TerrainComp> preparedCompilers,
        List<Heightmap> preparedHeightmaps)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            hash = MixBatchFingerprint(hash, GetSettingsFingerprint(settings));
            hash = MixBatchFingerprint(hash, Mathf.RoundToInt(position.x * 1000f));
            hash = MixBatchFingerprint(hash, Mathf.RoundToInt(position.y * 1000f));
            hash = MixBatchFingerprint(hash, Mathf.RoundToInt(position.z * 1000f));
            hash = MixBatchFingerprint(hash, Mathf.RoundToInt(rotation.eulerAngles.y * 1000f));
            hash = MixBatchFingerprint(hash, preparedHeightmaps.Count);
            for (int index = 0; index < preparedHeightmaps.Count; index++)
            {
                hash = MixBatchFingerprint(hash, preparedHeightmaps[index].GetInstanceID());
                hash = MixBatchFingerprint(hash, preparedCompilers[index].GetInstanceID());
            }

            return hash;
        }
    }

    private static ulong MixBatchFingerprint(ulong hash, int value)
    {
        return (hash ^ (uint)value) * 1099511628211UL;
    }

    private static bool IsFootprintCoveredByHeightmaps(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        List<Heightmap> loadedHeightmaps,
        List<Heightmap> preparedHeightmaps,
        bool includeCircleBoundary = false)
    {
        Heightmap anchor = null!;
        foreach (Heightmap heightmap in loadedHeightmaps)
        {
            if (heightmap && !heightmap.IsDistantLod && heightmap.IsPointInside(position))
            {
                anchor = heightmap;
                break;
            }
        }

        if (!anchor || !preparedHeightmaps.Contains(anchor))
        {
            return false;
        }

        float tileSize = anchor.m_width * anchor.m_scale;
        if (tileSize <= 0f || float.IsNaN(tileSize) || float.IsInfinity(tileSize))
        {
            return false;
        }

        float halfTile = tileSize * 0.5f;
        float radius = settings.SearchRadius;
        Vector3 anchorPosition = anchor.transform.position;
        int minTileX = Mathf.CeilToInt((position.x - radius - halfTile - anchorPosition.x) / tileSize - 0.001f);
        int maxTileX = Mathf.FloorToInt((position.x + radius + halfTile - anchorPosition.x) / tileSize + 0.001f);
        int minTileZ = Mathf.CeilToInt((position.z - radius - halfTile - anchorPosition.z) / tileSize - 0.001f);
        int maxTileZ = Mathf.FloorToInt((position.z + radius + halfTile - anchorPosition.z) / tileSize + 0.001f);
        float centerTolerance = Mathf.Max(anchor.m_scale * 0.01f, 0.001f);
        bool intersectsAnyTile = false;

        for (int tileZ = minTileZ; tileZ <= maxTileZ; tileZ++)
        {
            for (int tileX = minTileX; tileX <= maxTileX; tileX++)
            {
                Vector3 tileCenter = new(
                    anchorPosition.x + tileX * tileSize,
                    anchorPosition.y,
                    anchorPosition.z + tileZ * tileSize);
                if (!FootprintIntersectsTile(
                        position,
                        rotation,
                        settings,
                        tileCenter,
                        tileSize,
                        includeCircleBoundary))
                {
                    continue;
                }

                intersectsAnyTile = true;
                bool covered = false;
                foreach (Heightmap preparedHeightmap in preparedHeightmaps)
                {
                    if (!preparedHeightmap)
                    {
                        continue;
                    }

                    float preparedTileSize = preparedHeightmap.m_width * preparedHeightmap.m_scale;
                    Vector3 preparedCenter = preparedHeightmap.transform.position;
                    if (Mathf.Abs(preparedTileSize - tileSize) <= centerTolerance &&
                        Mathf.Abs(preparedCenter.x - tileCenter.x) <= centerTolerance &&
                        Mathf.Abs(preparedCenter.z - tileCenter.z) <= centerTolerance)
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                {
                    return false;
                }
            }
        }

        return intersectsAnyTile;
    }

    private static bool FootprintIntersectsHeightmap(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        Heightmap heightmap,
        bool includeCircleBoundary = false)
    {
        float tileSize = heightmap.m_width * heightmap.m_scale;
        return tileSize > 0f &&
               FootprintIntersectsTile(
                   position,
                   rotation,
                   settings,
                   heightmap.transform.position,
                   tileSize,
                   includeCircleBoundary);
    }

    private static bool FootprintIntersectsTile(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        Vector3 tileCenter,
        float tileSize,
        bool includeCircleBoundary = false)
    {
        if (settings.HasCircleFootprint)
        {
            bool includeBoundary = includeCircleBoundary ||
                                   (settings.Mode != TerrainProxyMode.Paint &&
                                    settings.TerrainEdgeSoftness <= 0f);
            return CircleIntersectsRect(
                position,
                settings.Radius,
                tileCenter,
                Quaternion.identity,
                tileSize,
                tileSize,
                includeBoundary);
        }

        return RectIntersectsRect(
            position,
            rotation,
            settings.Width,
            settings.Length,
            tileCenter,
            Quaternion.identity,
            tileSize,
            tileSize);
    }

    private static bool TryResetTerrainAndIntersectingProxyObjects(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        TerrainResetScope resetScope,
        ZNetView? placedView,
        out int terrainCompilers,
        out int removed,
        out string failureReason)
    {
        terrainCompilers = 0;
        removed = 0;
        failureReason = string.Empty;
        List<PreparedTerrainReset> preparedResets = [];
        if (!TryCollectIntersectingPersistentProxyViews(
                position,
                rotation,
                settings,
                resetScope,
                placedView,
                out List<LoadedProxyResetCandidate> proxyCandidates,
                out failureReason))
        {
            return false;
        }

        TerrainResetChannels resetChannels = resetScope == TerrainResetScope.PaintOnly
            ? TerrainResetChannels.Paint
            : TerrainResetChannels.Height | TerrainResetChannels.Paint;
        if (!TryPrepareTerrainReset(
                position,
                rotation,
                settings,
                resetChannels,
                out PreparedTerrainReset preparedReset,
                out failureReason))
        {
            return false;
        }

        preparedResets.Add(preparedReset);
        foreach (LoadedProxyResetCandidate candidate in proxyCandidates)
        {
            if (!TryPrepareTerrainReset(
                    candidate.Position,
                    candidate.Rotation,
                    candidate.Settings,
                    candidate.Channels,
                    out PreparedTerrainReset preparedProxyReset,
                    out failureReason))
            {
                return false;
            }

            preparedResets.Add(preparedProxyReset);
        }

        HashSet<TerrainComp> claimedCompilers = [];
        foreach (PreparedTerrainReset reset in preparedResets)
        {
            foreach (PreparedTerrainResetCompiler preparedCompiler in reset.Compilers)
            {
                if (claimedCompilers.Add(preparedCompiler.Compiler) &&
                    !TryClaimTerrainCompilerOwnership(preparedCompiler.Compiler))
                {
                    failureReason = "a required terrain compiler could not be claimed";
                    return false;
                }
            }
        }

        foreach (LoadedProxyResetCandidate candidate in proxyCandidates)
        {
            ZNetView view = candidate.View;
            if (!view ||
                !view.IsValid() ||
                !ReferenceEquals(view.GetZDO(), candidate.Zdo))
            {
                failureReason = "an intersecting terrain proxy became unavailable";
                return false;
            }

            if (!view.IsOwner())
            {
                view.ClaimOwnership();
            }

            if (!view.IsOwner())
            {
                failureReason = "an intersecting terrain proxy could not be claimed";
                return false;
            }
        }

        HashSet<TerrainComp> changedCompilers = [];
        foreach (PreparedTerrainReset reset in preparedResets)
        {
            bool areaChanged = false;
            foreach (PreparedTerrainResetCompiler preparedCompiler in reset.Compilers)
            {
                if (!ResetTerrainCompiler(
                        preparedCompiler.Compiler,
                        preparedCompiler.Heightmap,
                        preparedCompiler.Width,
                        reset.Position,
                        reset.Rotation,
                        reset.Settings,
                        reset.Channels))
                {
                    continue;
                }

                changedCompilers.Add(preparedCompiler.Compiler);
                areaChanged = true;
            }

            if (areaChanged)
            {
                ClutterSystem.instance?.ResetGrass(
                    reset.Position,
                    reset.Settings.SearchRadius + SearchPadding);
            }
        }

        terrainCompilers = changedCompilers.Count;
        foreach (LoadedProxyResetCandidate candidate in proxyCandidates)
        {
            DestroyProxy(candidate.View);
            removed++;
        }

        return true;
    }

    private static bool TryCollectIntersectingPersistentProxyViews(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        TerrainResetScope resetScope,
        ZNetView? placedView,
        out List<LoadedProxyResetCandidate> candidates,
        out string failureReason)
    {
        candidates = [];
        failureReason = string.Empty;
        ZNetScene scene = ZNetScene.instance;
        ZDOMan zdoMan = ZDOMan.instance;
        if (scene == null || zdoMan == null)
        {
            failureReason = "terrain proxy state is not ready";
            return false;
        }

        float searchRadius = settings.SearchRadius + MaxPersistentProxyFootprintRadius;
        Vector2i minZone = ZoneSystem.GetZone(
            new Vector3(position.x - searchRadius, position.y, position.z - searchRadius));
        Vector2i maxZone = ZoneSystem.GetZone(
            new Vector3(position.x + searchRadius, position.y, position.z + searchRadius));
        List<ZDO> knownZdos = [];
        for (int zoneY = minZone.y; zoneY <= maxZone.y; zoneY++)
        {
            for (int zoneX = minZone.x; zoneX <= maxZone.x; zoneX++)
            {
                zdoMan.FindObjects(new Vector2i(zoneX, zoneY), knownZdos);
            }
        }

        // A remote client retains unloaded known ZDOs in this outside-sector bucket.
        zdoMan.FindObjects(new Vector2i(int.MinValue, int.MinValue), knownZdos);
        // Include live keys as a fail-closed check for inconsistent sector bookkeeping.
        knownZdos.AddRange(scene.m_instances.Keys);
        HashSet<ZDOID> seen = [];
        foreach (ZDO zdo in knownZdos)
        {
            if (zdo == null ||
                !zdo.IsValid() ||
                !IsPersistentProxyPrefabHash(zdo.GetPrefab()) ||
                !zdo.Persistent ||
                !seen.Add(zdo.m_uid))
            {
                continue;
            }

            Vector3 proxyPosition = zdo.GetPosition();
            if (!IsFinite(proxyPosition) ||
                Utils.DistanceXZ(position, proxyPosition) > searchRadius)
            {
                continue;
            }

            Quaternion proxyRotation = zdo.GetRotation();
            if (!TryReadPersistentProxyResetSettings(
                    zdo,
                    proxyRotation,
                    out TerrainProxySettings proxySettings))
            {
                failureReason = "an intersecting terrain proxy has invalid stored settings";
                return false;
            }

            TerrainResetChannels proxyChannels = GetTerrainResetChannels(proxySettings);
            if (proxyChannels == TerrainResetChannels.None ||
                (resetScope == TerrainResetScope.PaintOnly &&
                 (proxyChannels & TerrainResetChannels.Paint) == 0) ||
                !FootprintsIntersect(
                    position,
                    rotation,
                    settings,
                    proxyPosition,
                    proxyRotation,
                    proxySettings))
            {
                continue;
            }

            if (!zdoMan.m_objectsByID.TryGetValue(zdo.m_uid, out ZDO canonicalZdo) ||
                !ReferenceEquals(canonicalZdo, zdo))
            {
                failureReason = "intersecting terrain proxy state is inconsistent";
                return false;
            }

            if (!scene.m_instances.TryGetValue(zdo, out ZNetView view) ||
                !view ||
                view == placedView ||
                !view.IsValid() ||
                !ReferenceEquals(view.GetZDO(), zdo) ||
                !IsProxyObject(view.gameObject))
            {
                failureReason = "an intersecting terrain proxy is not loaded";
                return false;
            }

            candidates.Add(new LoadedProxyResetCandidate(
                zdo,
                view,
                proxyPosition,
                proxyRotation,
                proxySettings,
                proxyChannels));
        }

        return true;
    }

    private static bool IsPersistentProxyPrefabHash(int prefabHash)
    {
        return prefabHash == PrefabHash ||
               prefabHash == SlopePrefabHash ||
               prefabHash == PaintPrefabHash;
    }

    private static bool TryReadPersistentProxyResetSettings(
        ZDO zdo,
        Quaternion rotation,
        out TerrainProxySettings settings)
    {
        settings = default;
        if (!HasStoredSettings(zdo) || !IsFinite(rotation))
        {
            return false;
        }

        settings = ReadSettings(zdo);
        int prefabHash = zdo.GetPrefab();
        bool modeMatchesPrefab =
            (prefabHash == PrefabHash && settings.Mode == TerrainProxyMode.Circle) ||
            (prefabHash == SlopePrefabHash && settings.Mode == TerrainProxyMode.Slope) ||
            (prefabHash == PaintPrefabHash && settings.Mode == TerrainProxyMode.Paint);
        return modeMatchesPrefab &&
               IsFinite(settings.Radius) &&
               IsFinite(settings.Width) &&
               IsFinite(settings.Length) &&
               IsFinite(settings.SlopeHeightDelta) &&
               IsFinite(settings.TerrainEdgeSoftness) &&
               settings.SearchRadius > 0f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return IsFinite(value.x) &&
               IsFinite(value.y) &&
               IsFinite(value.z) &&
               IsFinite(value.w);
    }

    private static bool TryPrepareTerrainReset(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        TerrainResetChannels channels,
        out PreparedTerrainReset preparedReset,
        out string failureReason)
    {
        preparedReset = new PreparedTerrainReset(position, rotation, settings, channels);
        failureReason = string.Empty;
        List<Heightmap> heightmaps = [];
        Heightmap.FindHeightmap(position, settings.SearchRadius + SearchPadding, heightmaps);

        List<Heightmap> preparedHeightmaps = [];
        HashSet<TerrainComp> seen = [];
        bool includeCircleBoundary = settings.Mode != TerrainProxyMode.Paint;
        foreach (Heightmap heightmap in heightmaps)
        {
            if (!heightmap ||
                heightmap.IsDistantLod ||
                !FootprintIntersectsHeightmap(
                    position,
                    rotation,
                    settings,
                    heightmap,
                    includeCircleBoundary))
            {
                continue;
            }

            TerrainComp compiler = heightmap.GetAndCreateTerrainCompiler();
            if (!compiler || !seen.Add(compiler) ||
                !TryInspectTerrainCompiler(compiler, out Heightmap preparedHeightmap, out int width) ||
                !ReferenceEquals(preparedHeightmap, heightmap))
            {
                failureReason = "a required terrain compiler is not ready";
                return false;
            }

            preparedHeightmaps.Add(preparedHeightmap);
            preparedReset.Compilers.Add(new PreparedTerrainResetCompiler(compiler, preparedHeightmap, width));
        }

        if (!IsFootprintCoveredByHeightmaps(
                position,
                rotation,
                settings,
                heightmaps,
                preparedHeightmaps,
                includeCircleBoundary))
        {
            failureReason = "the complete terrain footprint is not loaded";
            return false;
        }

        return true;
    }

    private static TerrainResetChannels GetTerrainResetChannels(TerrainProxySettings settings)
    {
        TerrainResetChannels channels = TerrainResetChannels.None;
        if (settings.ModifiesHeight)
        {
            channels |= TerrainResetChannels.Height;
        }

        if (settings.ModifiesPaint)
        {
            channels |= TerrainResetChannels.Paint;
        }

        return channels;
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
        float rectLength,
        bool includeBoundary = true)
    {
        Vector3 local = InverseYaw(rectRotation) * (circlePosition - rectPosition);
        float x = Mathf.Clamp(local.x, rectWidth * -0.5f, rectWidth * 0.5f);
        float z = Mathf.Clamp(local.z, rectLength * -0.5f, rectLength * 0.5f);
        float dx = local.x - x;
        float dz = local.z - z;
        float distanceSquared = dx * dx + dz * dz;
        float radiusSquared = radius * radius;
        return includeBoundary
            ? distanceSquared <= radiusSquared
            : distanceSquared < radiusSquared;
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

    private static bool ApplyTerrainCompiler(
        TerrainComp compiler,
        Heightmap heightmap,
        int width,
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings)
    {
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
                bool affected = TryGetAffectedTerrainNode(
                    position,
                    inverseYaw,
                    settings,
                    node,
                    out Vector3 local,
                    out float terrainNormalized);

                if (settings.ModifiesHeight && affected)
                {
                    float targetHeight = settings.GetTargetHeight(position, heightmap.transform.position.y, local.z);
                    float height = heightmap.GetHeight(x, z);
                    changed |= ApplyHeightNode(compiler, index, height, targetHeight, local.x, local.z, settings);
                }

                if (settings.ModifiesPaint && affected && HasPaintInfluence(terrainNormalized))
                {
                    changed |= ApplyPaintNode(compiler, heightmap, x, z, index, terrainNormalized, settings);
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        CommitTerrainCompiler(compiler, heightmap, position, settings.SearchRadius);
        return true;
    }

    private static bool ResetTerrainCompiler(
        TerrainComp compiler,
        Heightmap heightmap,
        int width,
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        TerrainResetChannels channels)
    {
        bool changed = false;
        bool resetHeight = (channels & TerrainResetChannels.Height) != 0;
        bool resetPaint = (channels & TerrainResetChannels.Paint) != 0;
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
                if (!TryGetAffectedTerrainNode(
                        position,
                        inverseYaw,
                        settings,
                        node,
                        out _,
                        out float normalized))
                {
                    continue;
                }

                if (resetHeight &&
                    (compiler.m_modifiedHeight[index] ||
                     Mathf.Abs(compiler.m_levelDelta[index]) > 0.001f ||
                     Mathf.Abs(compiler.m_smoothDelta[index]) > 0.001f))
                {
                    compiler.m_modifiedHeight[index] = false;
                    compiler.m_levelDelta[index] = 0f;
                    compiler.m_smoothDelta[index] = 0f;
                    changed = true;
                }

                if (resetPaint &&
                    compiler.m_modifiedPaint[index] &&
                    (settings.Mode != TerrainProxyMode.Paint || HasPaintInfluence(normalized)))
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

        CommitTerrainCompiler(compiler, heightmap, position, settings.SearchRadius);
        return true;
    }

    private static bool TryInspectTerrainCompiler(TerrainComp compiler, out Heightmap heightmap, out int width)
    {
        heightmap = null!;
        width = 0;
        if (!compiler.m_nview || !compiler.m_nview.IsValid() || !compiler.m_hmap)
        {
            return false;
        }

        heightmap = compiler.m_hmap;
        width = heightmap.m_width + 1;
        int count = width * width;
        return compiler.m_modifiedHeight != null &&
               compiler.m_levelDelta != null &&
               compiler.m_smoothDelta != null &&
               compiler.m_modifiedPaint != null &&
               compiler.m_paintMask != null &&
               compiler.m_modifiedHeight.Length == count &&
               compiler.m_levelDelta.Length == count &&
               compiler.m_smoothDelta.Length == count &&
               compiler.m_modifiedPaint.Length == count &&
               compiler.m_paintMask.Length == count;
    }

    private static bool TryClaimTerrainCompilerOwnership(TerrainComp compiler)
    {
        if (!compiler.m_nview || !compiler.m_nview.IsValid())
        {
            return false;
        }

        if (!compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        return compiler.m_nview.IsOwner();
    }

    private static void CommitTerrainCompiler(
        TerrainComp compiler,
        Heightmap heightmap,
        Vector3 position,
        float radius)
    {
        compiler.m_operations++;
        compiler.m_lastOpPoint = position;
        compiler.m_lastOpRadius = radius;
        compiler.Save();
        heightmap.Poke(delayed: false);
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

    private static bool TryGetAffectedTerrainNode(
        Vector3 center,
        Quaternion inverseYaw,
        TerrainProxySettings settings,
        Vector3 node,
        out Vector3 local,
        out float normalized)
    {
        local = inverseYaw * (node - center);
        normalized = settings.GetNormalizedDistance(local.x, local.z);
        return normalized <= 1f;
    }

    private static bool HasPaintInfluence(float normalized)
    {
        return normalized < 1f;
    }

    private static bool Approximately(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.001f &&
               Mathf.Abs(a.g - b.g) < 0.001f &&
               Mathf.Abs(a.b - b.b) < 0.001f &&
               Mathf.Abs(a.a - b.a) < 0.001f;
    }

    [Flags]
    private enum TerrainResetChannels
    {
        None = 0,
        Height = 1,
        Paint = 2
    }

    private sealed class PreparedTerrainReset
    {
        public PreparedTerrainReset(
            Vector3 position,
            Quaternion rotation,
            TerrainProxySettings settings,
            TerrainResetChannels channels)
        {
            Position = position;
            Rotation = rotation;
            Settings = settings;
            Channels = channels;
        }

        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public TerrainProxySettings Settings { get; }
        public TerrainResetChannels Channels { get; }
        public List<PreparedTerrainResetCompiler> Compilers { get; } = [];
    }

    private readonly struct LoadedProxyResetCandidate
    {
        public LoadedProxyResetCandidate(
            ZDO zdo,
            ZNetView view,
            Vector3 position,
            Quaternion rotation,
            TerrainProxySettings settings,
            TerrainResetChannels channels)
        {
            Zdo = zdo;
            View = view;
            Position = position;
            Rotation = rotation;
            Settings = settings;
            Channels = channels;
        }

        public ZDO Zdo { get; }
        public ZNetView View { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public TerrainProxySettings Settings { get; }
        public TerrainResetChannels Channels { get; }
    }

    private readonly struct PreparedTerrainResetCompiler
    {
        public PreparedTerrainResetCompiler(TerrainComp compiler, Heightmap heightmap, int width)
        {
            Compiler = compiler;
            Heightmap = heightmap;
            Width = width;
        }

        public TerrainComp Compiler { get; }
        public Heightmap Heightmap { get; }
        public int Width { get; }
    }
}

[HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.ApplyToHeightmap))]
internal static class AdminTerrainToolTerrainCompPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo limit = AccessTools.Method(typeof(AdminTerrainTool), nameof(AdminTerrainTool.GetTerrainCompHeightClampLimit));
        if (limit == null)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning(
                "ZoneSavior terrain clamp patch skipped because the replacement method could not be resolved.");
            return code;
        }

        const int expectedMatches = 2;
        List<CodeInstruction> matches = code
            .Where(instruction =>
                instruction.opcode == OpCodes.Ldc_R4 &&
                instruction.operand is float value &&
                Math.Abs(value - 8f) < 0.001f)
            .ToList();
        if (matches.Count != expectedMatches)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning(
                $"ZoneSavior terrain clamp patch expected {expectedMatches} clamp constants but found {matches.Count}; leaving original IL unchanged.");
            return code;
        }

        foreach (CodeInstruction instruction in matches)
        {
            instruction.opcode = OpCodes.Call;
            instruction.operand = limit;
        }

        return code;
    }
}

internal static class AdminTerrainToolZNetDiagnostics
{
    private const int MaxLoggedZdos = 32;
    private const int ScanIntervalFrames = 30;

    private static readonly HashSet<ZDOID> LoggedDestroyZdos = [];
    private static readonly HashSet<ZDOID> LoggedRemoveZdos = [];
    private static ZNetScene? _diagnosticScene;
    private static int _nextScanFrame;

    public static void InspectDestroyingView(ZNetView view)
    {
        try
        {
            ZNetScene scene = ZNetScene.instance;
            ZDO zdo = view.m_zdo;
            if (scene == null || zdo == null)
            {
                return;
            }

            EnsureScene(scene);
            if (!scene.m_instances.TryGetValue(zdo, out ZNetView registeredView) ||
                !ReferenceEquals(registeredView, view))
            {
                return;
            }

            LogStaleZdo(
                scene,
                zdo,
                $"registered ZNetView is being destroyed without prior unregister; viewGhost={view.m_ghost}",
                LoggedDestroyZdos);
        }
        catch (Exception ex)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogDebug($"ZoneSavior ZNet diagnostic could not inspect OnDestroy: {ex.Message}");
        }
    }

    public static void InspectRegisteredViews(
        ZNetScene scene,
        List<ZDO> currentNearObjects,
        List<ZDO> currentDistantObjects)
    {
        EnsureScene(scene);
        if (LoggedRemoveZdos.Count >= MaxLoggedZdos || Time.frameCount < _nextScanFrame)
        {
            return;
        }

        _nextScanFrame = Time.frameCount + ScanIntervalFrames;
        try
        {
            foreach (KeyValuePair<ZDO, ZNetView> entry in scene.m_instances)
            {
                ZNetView view = entry.Value;
                if (!ReferenceEquals(view, null) && view)
                {
                    continue;
                }

                LogStaleZdo(
                    scene,
                    entry.Key,
                    ReferenceEquals(view, null)
                        ? $"m_instances contains a CLR-null ZNetView before RemoveObjects; " +
                          $"near={currentNearObjects.Contains(entry.Key)}, distant={currentDistantObjects.Contains(entry.Key)}"
                        : $"m_instances contains a destroyed Unity ZNetView before RemoveObjects; " +
                          $"near={currentNearObjects.Contains(entry.Key)}, distant={currentDistantObjects.Contains(entry.Key)}",
                    LoggedRemoveZdos);

                if (LoggedRemoveZdos.Count >= MaxLoggedZdos)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogDebug($"ZoneSavior ZNet diagnostic could not inspect m_instances: {ex.Message}");
        }
    }

    private static void EnsureScene(ZNetScene scene)
    {
        if (ReferenceEquals(_diagnosticScene, scene))
        {
            return;
        }

        _diagnosticScene = scene;
        LoggedDestroyZdos.Clear();
        LoggedRemoveZdos.Clear();
        _nextScanFrame = 0;
    }

    private static void LogStaleZdo(
        ZNetScene scene,
        ZDO zdo,
        string reason,
        HashSet<ZDOID> loggedZdos)
    {
        if (zdo == null || loggedZdos.Count >= MaxLoggedZdos || !loggedZdos.Add(zdo.m_uid))
        {
            return;
        }

        int prefabHash = zdo.GetPrefab();
        GameObject prefab = scene.GetPrefab(prefabHash);
        string prefabName = prefab ? prefab.name : "<unknown>";
        Vector3 position = zdo.GetPosition();
        ZDO? pending = ZNetView.m_initZDO;
        string pendingUid = pending == null ? "none" : pending.m_uid.ToString();

        ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning(
            $"ZoneSavior ZNet diagnostic: {reason}. " +
            $"zdo={zdo.m_uid}, prefab={prefabName} ({prefabHash}), " +
            $"position=({position.x:0.##}, {position.y:0.##}, {position.z:0.##}), " +
            $"created={zdo.Created}, persistent={zdo.Persistent}, distant={zdo.Distant}, type={zdo.Type}, " +
            $"ghostInit={ZNetView.m_ghostInit}, useInitZDO={ZNetView.m_useInitZDO}, " +
            $"pendingZdo={pendingUid}, registeredInstances={scene.m_instances.Count}, frame={Time.frameCount}.");
    }
}

[HarmonyPatch(typeof(ZNetView), "OnDestroy")]
internal static class AdminTerrainToolZNetViewDestroyDiagnosticPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(ZNetView __instance)
    {
        AdminTerrainToolZNetDiagnostics.InspectDestroyingView(__instance);
    }
}

[HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
internal static class AdminTerrainToolZNetSceneRemoveDiagnosticPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(
        ZNetScene __instance,
        List<ZDO> __0,
        List<ZDO> __1)
    {
        AdminTerrainToolZNetDiagnostics.InspectRegisteredViews(
            __instance,
            __0,
            __1);
    }
}

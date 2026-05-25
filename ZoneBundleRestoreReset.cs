using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DataHelper = ZoneSavior.ZoneBundleZdoHelper;

namespace ZoneSavior;

internal static partial class ZoneBundleCommands
{
    internal static ZoneBundleCommandResult RestoreTagToOriginalZones(string tag)
    {
        ZoneBundleManifest manifest = ZoneBundleStore.LoadManifest(tag);
        List<LoadWorkItem> work = [];
        foreach (ZoneBundleManifestEntry entry in manifest.Bundles)
        {
            Vector2i sourceZone = ToVector2i(entry.Zone);
            ZoneBundleFile bundle = ZoneBundleStore.LoadBundleFromManifestEntry(tag, entry);
            work.Add(new LoadWorkItem(sourceZone, bundle));
        }

        ZoneLoadTotals totals = ApplyLoadWork(work, exactSource: true, 0f);

        return ZoneBundleCommandResult.Ok(
            $"Restored {work.Count} archived zone bundle(s) for tag '{tag}' " +
            $"(removed: {totals.Removed}, created: {totals.Created}, terrain: {totals.TerrainApplied}/{work.Count}).");
    }

    internal static IEnumerator RestoreTagToOriginalZonesAsync(string tag, Action<ZoneBundleCommandResult> onComplete)
    {
        ZoneBundleManifest manifest;
        try
        {
            manifest = ZoneBundleStore.LoadManifest(tag);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle async archive restore failed: {ex}");
            onComplete(ZoneBundleCommandResult.Fail(ex.Message));
            yield break;
        }

        List<LoadWorkItem> work = [];
        foreach (ZoneBundleManifestEntry entry in manifest.Bundles)
        {
            Vector2i sourceZone = ToVector2i(entry.Zone);
            if (!ZoneBundleStore.TryLoadBundleFromManifestEntry(tag, entry, out ZoneBundleFile bundle, out string bundleReason))
            {
                _logger.LogError($"Zone bundle async archive restore failed: {bundleReason}");
                onComplete(ZoneBundleCommandResult.Fail(bundleReason));
                yield break;
            }

            work.Add(new LoadWorkItem(sourceZone, bundle));
            yield return null;
        }

        ZoneLoadTotals totals = default;
        string restoreError = "";
        yield return PrepareAndApplyLocalLoadWorkAsync("Zone bundle async archive restore failed", work, exactSource: true, 0f, (loadTotals, error) =>
        {
            totals = loadTotals;
            restoreError = error;
        });
        if (!string.IsNullOrWhiteSpace(restoreError))
        {
            onComplete(ZoneBundleCommandResult.Fail(restoreError));
            yield break;
        }

        onComplete(ZoneBundleCommandResult.Ok(
            $"Restored {work.Count} archived zone bundle(s) for tag '{tag}' " +
            $"(removed: {totals.Removed}, created: {totals.Created}, terrain: {totals.TerrainApplied}/{work.Count})."));
    }

    internal static string MakeUniqueAutoArchiveTag(string preferredTag)
    {
        if (!ZoneBundleStore.ArchiveTagExists(preferredTag))
        {
            return preferredTag;
        }

        for (int index = 2; index <= 999; index++)
        {
            string candidate = $"{preferredTag}_n{index:D3}";
            if (!ZoneBundleStore.ArchiveTagExists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find a free archive tag for '{preferredTag}'.");
    }

    internal static ZoneBundleResetResult ResetGeneratedZones(IEnumerable<Vector2i> sourceZones)
    {
        List<Vector2i> zones = NormalizeZones(sourceZones);

        if (zones.Count == 0)
        {
            return new ZoneBundleResetResult
            {
                Success = false,
                Message = "No zones to reset."
            };
        }

        HashSet<ZDOID> characterIds = GetOnlineCharacterIds();
        HashSet<Vector2i> zoneSet = zones.ToHashSet();
        int removed = ResetZoneObjects(zoneSet, characterIds);

        foreach (Vector2i zone in zones)
        {
            ResetZoneSystemState(zone);
        }

        ResetVerificationResult verification = VerifyResetObjects(zoneSet, characterIds);
        removed += verification.Removed;

        ClutterSystem.instance?.ClearAll();
        RecalculateLoadedTerrain();
        Minimap.instance?.UpdateLocationPins(1000f);

        return BuildResetResult(zones.Count, removed, verification.Removed, verification.RemainingWearNTear);
    }

    internal static IEnumerator ResetGeneratedZonesAsync(IEnumerable<Vector2i> sourceZones, Action<ZoneBundleResetResult> onComplete)
    {
        List<Vector2i> zones = NormalizeZones(sourceZones);

        if (zones.Count == 0)
        {
            onComplete(new ZoneBundleResetResult
            {
                Success = false,
                Message = "No zones to reset."
            });
            yield break;
        }

        HashSet<ZDOID> characterIds = GetOnlineCharacterIds();
        HashSet<Vector2i> zoneSet = zones.ToHashSet();
        int removed = 0;
        yield return ResetZoneObjectsAsync(zoneSet, characterIds, value => removed = value);

        int zonesSinceYield = 0;
        foreach (Vector2i zone in zones)
        {
            ResetZoneSystemState(zone);
            zonesSinceYield++;
            if (zonesSinceYield >= TerrainRecalcBatchSize)
            {
                zonesSinceYield = 0;
                yield return null;
            }
        }

        ResetVerificationResult verification = default;
        yield return VerifyResetObjectsAsync(zoneSet, characterIds, value => verification = value);
        removed += verification.Removed;

        ClutterSystem.instance?.ClearAll();
        yield return RecalculateLoadedTerrainAsync();
        Minimap.instance?.UpdateLocationPins(1000f);

        onComplete(BuildResetResult(zones.Count, removed, verification.Removed, verification.RemainingWearNTear));
    }

    private static ResetVerificationResult VerifyResetObjects(HashSet<Vector2i> zoneSet, HashSet<ZDOID> characterIds)
    {
        int remainingWearNTear = CountRemainingCreatorWearNTear(zoneSet, characterIds);
        if (remainingWearNTear <= 0)
        {
            return new ResetVerificationResult(0, remainingWearNTear);
        }

        int verificationRemoved = ResetZoneObjects(zoneSet, characterIds);
        remainingWearNTear = CountRemainingCreatorWearNTear(zoneSet, characterIds);
        return new ResetVerificationResult(verificationRemoved, remainingWearNTear);
    }

    private static IEnumerator VerifyResetObjectsAsync(HashSet<Vector2i> zoneSet, HashSet<ZDOID> characterIds, Action<ResetVerificationResult> onComplete)
    {
        int remainingWearNTear = 0;
        yield return CountRemainingCreatorWearNTearAsync(zoneSet, characterIds, value => remainingWearNTear = value);
        if (remainingWearNTear <= 0)
        {
            onComplete(new ResetVerificationResult(0, remainingWearNTear));
            yield break;
        }

        int verificationRemoved = 0;
        yield return ResetZoneObjectsAsync(zoneSet, characterIds, value => verificationRemoved = value);
        yield return CountRemainingCreatorWearNTearAsync(zoneSet, characterIds, value => remainingWearNTear = value);
        onComplete(new ResetVerificationResult(verificationRemoved, remainingWearNTear));
    }

    private static ZoneBundleResetResult BuildResetResult(int zoneCount, int removed, int verificationRemoved, int remainingWearNTear)
    {
        string message = $"Reset {zoneCount} generated zone(s), removed {removed} ZDO(s).";
        if (verificationRemoved > 0)
        {
            message += $" Verification pass removed {verificationRemoved} ZDO(s).";
            _logger.LogWarning(message);
        }

        if (remainingWearNTear > 0)
        {
            message += $" {remainingWearNTear} creator WearNTear ZDO(s) still remain after reset.";
            _logger.LogWarning(message);
        }

        return new ZoneBundleResetResult
        {
            Success = remainingWearNTear == 0,
            ZoneCount = zoneCount,
            RemovedCount = removed,
            RemainingWearNTearCount = remainingWearNTear,
            Message = message
        };
    }

    private static int ClearTargetZone(Vector2i targetZone)
    {
        List<ZDO> objects = new();
        ZDOMan.instance.FindObjects(targetZone, objects);

        int removed = 0;
        foreach (ZDO zdo in objects.ToList())
        {
            removed += TryDestroyOverwritableZdo(zdo) ? 1 : 0;
        }

        DataHelper.FlushDestroyed();
        return removed;
    }

    private static IEnumerator ClearTargetZoneAsync(Vector2i targetZone, Action<int> onComplete)
    {
        List<ZDO> objects = [];
        ZDOMan.instance.FindObjects(targetZone, objects);

        int removed = 0;
        int processedSinceYield = 0;
        foreach (ZDO zdo in objects.ToList())
        {
            removed += TryDestroyOverwritableZdo(zdo) ? 1 : 0;

            processedSinceYield++;
            if (processedSinceYield >= ResetBatchSize)
            {
                DataHelper.FlushDestroyed();
                processedSinceYield = 0;
                yield return null;
            }
        }

        DataHelper.FlushDestroyed();
        onComplete(removed);
    }

    private static bool TryDestroyOverwritableZdo(ZDO zdo)
    {
        if (zdo == null || !zdo.IsValid())
        {
            return false;
        }

        GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        if (!prefab || !ShouldDeleteForOverwrite(prefab, zdo))
        {
            return false;
        }

        DataHelper.Destroy(zdo);
        return true;
    }

    private static void ResetZoneSystemState(Vector2i zone)
    {
        if (ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance location))
        {
            location.m_placed = false;
            location.m_position = new Vector3(
                location.m_position.x,
                WorldGenerator.instance.GetHeight(location.m_position.x, location.m_position.z),
                location.m_position.z);
            ZoneSystem.instance.m_locationInstances[zone] = location;
        }

        ZoneSystem.instance.m_generatedZones.Remove(zone);
        if (ZoneSystem.instance.m_zones.TryGetValue(zone, out ZoneSystem.ZoneData zoneData))
        {
            UnityEngine.Object.Destroy(zoneData.m_root);
            ZoneSystem.instance.m_zones.Remove(zone);
        }
    }

    private static int ResetZoneObjects(HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds)
    {
        List<ZDO> objects = GetResetZoneObjects(zones);

        int removed = 0;
        foreach (ZDO zdo in objects)
        {
            if (!IsResettableZoneObject(zdo, zones, protectedCharacterIds))
            {
                continue;
            }

            DataHelper.Destroy(zdo);
            removed++;
        }

        DataHelper.FlushDestroyed();
        return removed;
    }

    private static IEnumerator ResetZoneObjectsAsync(HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds, Action<int> onComplete)
    {
        HashSet<ZDOID> seen = [];
        List<ZDO> zoneObjects = [];
        int removed = 0;
        int processedSinceYield = 0;
        foreach (Vector2i zone in zones)
        {
            zoneObjects.Clear();
            ZDOMan.instance.FindObjects(zone, zoneObjects);
            foreach (ZDO zdo in zoneObjects)
            {
                processedSinceYield++;
                if (processedSinceYield >= ResetBatchSize)
                {
                    DataHelper.FlushDestroyed();
                    processedSinceYield = 0;
                    yield return null;
                }

                if (!TryCollectResetZoneObject(zdo, zones, seen, out ZDO resetObject))
                {
                    continue;
                }

                if (IsResettableZoneObject(resetObject, zones, protectedCharacterIds))
                {
                    DataHelper.Destroy(resetObject);
                    removed++;
                }
            }
        }

        DataHelper.FlushDestroyed();
        onComplete(removed);
    }

    private static int CountRemainingCreatorWearNTear(HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds)
    {
        List<ZDO> objects = GetResetZoneObjects(zones);

        int count = 0;
        foreach (ZDO zdo in objects)
        {
            if (!IsResettableZoneObject(zdo, zones, protectedCharacterIds))
            {
                continue;
            }

            if (!IsCreatorWearNTear(zdo))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    private static IEnumerator CountRemainingCreatorWearNTearAsync(HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds, Action<int> onComplete)
    {
        HashSet<ZDOID> seen = [];
        List<ZDO> zoneObjects = [];
        int count = 0;
        int processedSinceYield = 0;
        foreach (Vector2i zone in zones)
        {
            zoneObjects.Clear();
            ZDOMan.instance.FindObjects(zone, zoneObjects);
            foreach (ZDO zdo in zoneObjects)
            {
                processedSinceYield++;
                if (processedSinceYield >= ResetBatchSize)
                {
                    processedSinceYield = 0;
                    yield return null;
                }

                if (!TryCollectResetZoneObject(zdo, zones, seen, out ZDO resetObject) ||
                    !IsResettableZoneObject(resetObject, zones, protectedCharacterIds) ||
                    !IsCreatorWearNTear(resetObject))
                {
                    continue;
                }

                count++;
            }
        }

        onComplete(count);
    }

    private static List<ZDO> GetResetZoneObjects(HashSet<Vector2i> zones)
    {
        List<ZDO> objects = [];
        HashSet<ZDOID> seen = [];
        List<ZDO> zoneObjects = [];
        foreach (Vector2i zone in zones)
        {
            zoneObjects.Clear();
            ZDOMan.instance.FindObjects(zone, zoneObjects);
            foreach (ZDO zdo in zoneObjects)
            {
                if (!TryCollectResetZoneObject(zdo, zones, seen, out ZDO resetObject))
                {
                    continue;
                }

                objects.Add(resetObject);
            }
        }

        return objects;
    }

    private static bool TryCollectResetZoneObject(ZDO zdo, HashSet<Vector2i> zones, HashSet<ZDOID> seen, out ZDO resetObject)
    {
        resetObject = null!;
        if (zdo == null ||
            !zdo.IsValid() ||
            !seen.Add(zdo.m_uid) ||
            !zones.Contains(ZoneSystem.GetZone(zdo.GetPosition())))
        {
            return false;
        }

        resetObject = zdo;
        return true;
    }

    private static bool IsResettableZoneObject(ZDO zdo, HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds)
    {
        return zdo != null
               && zdo.IsValid()
               && !protectedCharacterIds.Contains(zdo.m_uid)
               && zones.Contains(ZoneSystem.GetZone(zdo.GetPosition()));
    }

    private static bool IsCreatorWearNTear(ZDO zdo)
    {
        if (zdo.GetLong(ZDOVars.s_creator, 0L) == 0L)
        {
            return false;
        }

        GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        return prefab && prefab.GetComponent<WearNTear>() != null;
    }

    private static HashSet<ZDOID> GetOnlineCharacterIds()
    {
        HashSet<ZDOID> ids = [];
        if (ZNet.instance == null)
        {
            return ids;
        }

        if (!ZNet.instance.m_characterID.IsNone())
        {
            ids.Add(ZNet.instance.m_characterID);
        }

        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (peer != null && peer.IsReady() && !peer.m_characterID.IsNone())
            {
                ids.Add(peer.m_characterID);
            }
        }

        return ids;
    }

    private static void RecalculateLoadedTerrain()
    {
        foreach (Heightmap heightmap in GetLoadedHeightmapSnapshot())
        {
            RecalculateHeightmap(heightmap);
        }
    }

    private static IEnumerator RecalculateLoadedTerrainAsync()
    {
        int processed = 0;
        foreach (Heightmap heightmap in GetLoadedHeightmapSnapshot())
        {
            if (!RecalculateHeightmap(heightmap))
            {
                continue;
            }

            processed++;
            if (processed >= TerrainRecalcBatchSize)
            {
                processed = 0;
                yield return null;
            }
        }
    }

    private static List<Heightmap> GetLoadedHeightmapSnapshot()
    {
        return Heightmap.s_heightmaps
            .Where(heightmap => heightmap)
            .ToList();
    }

    private static bool RecalculateHeightmap(Heightmap heightmap)
    {
        if (!heightmap)
        {
            return false;
        }

        try
        {
            heightmap.m_buildData = null;
            heightmap.Poke(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to recalculate loaded terrain heightmap: {ex.Message}");
            return false;
        }
    }
}

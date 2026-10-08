using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DataHelper = ZoneSavior.ZoneBundleZdoHelper;

namespace ZoneSavior;

internal static partial class ZoneBundleCommands
{
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

    internal static IEnumerator ResetGeneratedZonesAsync(IEnumerable<Vector2i> sourceZones, Action<ZoneBundleResetResult> onComplete,
        Func<Vector2i, bool>? canResetZone = null, ISet<ZDOID>? destroyedIds = null, Action? onBeforeFirstMutation = null)
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
        string? failure = null;
        bool mutationStarted = false;
        void BeginMutation()
        {
            if (mutationStarted) return;
            onBeforeFirstMutation?.Invoke();
            mutationStarted = true;
        }

        yield return ResetZoneObjectsAsync(zoneSet, characterIds, canResetZone, destroyedIds, BeginMutation, (value, reason) =>
        {
            removed = value;
            failure = reason;
        });
        if (failure != null)
        {
            onComplete(BuildInterruptedResetResult(zones.Count, removed, failure));
            yield break;
        }

        int zonesSinceYield = 0;
        foreach (Vector2i zone in zones)
        {
            if (!CanContinueReset(zone, canResetZone, out failure))
            {
                onComplete(BuildInterruptedResetResult(zones.Count, removed, failure!));
                yield break;
            }

            BeginMutation();
            ResetZoneSystemState(zone);
            zonesSinceYield++;
            if (zonesSinceYield >= TerrainRecalcBatchSize)
            {
                zonesSinceYield = 0;
                yield return null;
            }
        }

        ResetVerificationResult verification = default;
        yield return VerifyResetObjectsAsync(zoneSet, characterIds, canResetZone, destroyedIds, BeginMutation, (value, reason) =>
        {
            verification = value;
            failure = reason;
        });
        removed += verification.Removed;
        if (failure != null)
        {
            onComplete(BuildInterruptedResetResult(zones.Count, removed, failure));
            yield break;
        }

        ClutterSystem.instance?.ClearAll();
        yield return RecalculateLoadedTerrainAsync();
        if (Minimap.instance != null) ZoneSaviorGameAccess.UpdateLocationPins(Minimap.instance, 1000f);

        foreach (Vector2i zone in zones)
        {
            if (!CanContinueReset(zone, canResetZone, out failure))
            {
                onComplete(BuildInterruptedResetResult(zones.Count, removed, failure!));
                yield break;
            }
        }

        onComplete(BuildResetResult(zones.Count, removed, verification.Removed, verification.RemainingWearNTear));
    }

    private static IEnumerator VerifyResetObjectsAsync(HashSet<Vector2i> zoneSet, HashSet<ZDOID> characterIds,
        Func<Vector2i, bool>? canResetZone, ISet<ZDOID>? destroyedIds, Action beforeMutation,
        Action<ResetVerificationResult, string?> onComplete)
    {
        int remainingWearNTear = 0;
        string? failure = null;
        yield return CountRemainingCreatorWearNTearAsync(zoneSet, characterIds, canResetZone, (value, reason) =>
        {
            remainingWearNTear = value;
            failure = reason;
        });
        if (failure != null)
        {
            onComplete(new ResetVerificationResult(0, remainingWearNTear), failure);
            yield break;
        }

        if (remainingWearNTear <= 0)
        {
            onComplete(new ResetVerificationResult(0, remainingWearNTear), null);
            yield break;
        }

        int verificationRemoved = 0;
        yield return ResetZoneObjectsAsync(zoneSet, characterIds, canResetZone, destroyedIds, beforeMutation, (value, reason) =>
        {
            verificationRemoved = value;
            failure = reason;
        });
        if (failure == null)
        {
            yield return CountRemainingCreatorWearNTearAsync(zoneSet, characterIds, canResetZone, (value, reason) =>
            {
                remainingWearNTear = value;
                failure = reason;
            });
        }

        onComplete(new ResetVerificationResult(verificationRemoved, remainingWearNTear), failure);
    }

    private static ZoneBundleResetResult BuildInterruptedResetResult(int zoneCount, int removed, string failure)
    {
        DataHelper.FlushDestroyed();
        return new ZoneBundleResetResult
        {
            Success = false,
            ZoneCount = zoneCount,
            RemovedCount = removed,
            Message = $"Reset interrupted after removing {removed} ZDO(s): {failure}"
        };
    }

    private static bool CanContinueReset(Vector2i zone, Func<Vector2i, bool>? canResetZone, out string? failure)
    {
        failure = null;
        if (canResetZone != null && !canResetZone(zone))
        {
            failure = $"Zone {zone} is no longer eligible for reset.";
            return false;
        }

        return true;
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

    private static IEnumerator ValidateClearTargetsAsync(HashSet<Vector2i> allowedTargetZones)
    {
        int processedSinceYield = 0;
        List<ZDO> objects = [];
        foreach (Vector2i zone in allowedTargetZones)
        {
            objects.Clear();
            ZoneSaviorZones.FindObjects(zone, objects);
            ZDOID[] objectIds = objects.Select(zdo => zdo.m_uid).ToArray();
            foreach (ZDOID id in objectIds)
            {
                ZDO? zdo = ZDOMan.instance.GetZDO(id);
                if (IsOverwritableZdo(zdo) &&
                    !DataHelper.CanDestroyWithinZones(zdo!, allowedTargetZones, out string? failure))
                {
                    throw new InvalidOperationException($"Load target validation failed: {failure}");
                }

                processedSinceYield++;
                if (processedSinceYield >= ResetBatchSize)
                {
                    processedSinceYield = 0;
                    yield return null;
                }
            }
        }
    }

    private static IEnumerator ClearTargetZoneAsync(Vector2i targetZone, Action<int> onComplete,
        HashSet<Vector2i>? allowedTargetZones = null)
    {
        if (allowedTargetZones != null && !allowedTargetZones.Contains(targetZone))
        {
            throw new InvalidOperationException($"Load target zone {targetZone} is outside the allowed target zones.");
        }

        List<ZDO> objects = [];
        ZoneSaviorZones.FindObjects(targetZone, objects);

        int removed = 0;
        int processedSinceYield = 0;
        foreach (ZDOID id in objects.Select(zdo => zdo.m_uid).ToArray())
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(id);
            removed += TryDestroyOverwritableZdo(zdo, allowedTargetZones) ? 1 : 0;

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

    private static bool IsOverwritableZdo(ZDO? zdo)
    {
        if (zdo == null || !zdo.IsValid())
        {
            return false;
        }

        GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        return prefab && ShouldDeleteForOverwrite(prefab, zdo);
    }

    private static bool TryDestroyOverwritableZdo(ZDO? zdo, HashSet<Vector2i>? allowedTargetZones = null)
    {
        if (!IsOverwritableZdo(zdo))
        {
            return false;
        }

        if (allowedTargetZones == null)
        {
            DataHelper.Destroy(zdo!);
        }
        else if (!DataHelper.TryDestroyWithinZones(zdo!, allowedTargetZones, out _, out string? failure))
        {
            DataHelper.FlushDestroyed();
            throw new InvalidOperationException($"Load target overwrite interrupted: {failure}");
        }

        return true;
    }

    private static void ResetZoneSystemState(Vector2i zone)
    {
        Vector2s gameZone = ZoneSaviorZones.ToGameZone(zone);
        if (ZoneSystem.instance.m_locationInstances.TryGetValue(gameZone, out ZoneSystem.LocationInstance location))
        {
            location.m_placed = false;
            location.m_position = new Vector3(
                location.m_position.x,
                WorldGenerator.instance.GetHeight(location.m_position.x, location.m_position.z),
                location.m_position.z);
            ZoneSystem.instance.m_locationInstances[gameZone] = location;
        }

        ZoneSaviorGameAccess.UnloadZone(ZoneSystem.instance, gameZone);
    }

    private static IEnumerator ResetZoneObjectsAsync(HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds,
        Func<Vector2i, bool>? canResetZone, ISet<ZDOID>? destroyedIds, Action beforeMutation, Action<int, string?> onComplete)
    {
        HashSet<ZDOID> seen = [];
        List<ZDO> zoneObjects = [];
        List<ZDOID> resetObjects = [];
        int removed = 0;
        int processedSinceYield = 0;
        string? failure;
        // Check every known chain before the first deletion. Recheck each chain at deletion too,
        // because a live connection or owner eligibility may change while this coroutine yields.
        foreach (Vector2i zone in zones)
        {
            if (!CanContinueReset(zone, canResetZone, out failure))
            {
                onComplete(removed, failure);
                yield break;
            }

            protectedCharacterIds.UnionWith(GetOnlineCharacterIds());
            zoneObjects.Clear();
            ZoneSaviorZones.FindObjects(zone, zoneObjects);
            foreach (ZDO zdo in zoneObjects)
            {
                processedSinceYield++;
                if (processedSinceYield >= ResetBatchSize)
                {
                    processedSinceYield = 0;
                    yield return null;
                    if (!CanContinueReset(zone, canResetZone, out failure))
                    {
                        onComplete(removed, failure);
                        yield break;
                    }

                    protectedCharacterIds.UnionWith(GetOnlineCharacterIds());
                }

                if (!TryCollectResetZoneObject(zdo, zones, seen, out ZDO resetObject))
                {
                    continue;
                }

                if (!protectedCharacterIds.Contains(resetObject.m_uid))
                {
                    if (!DataHelper.CanDestroyWithinZones(resetObject, zones, out failure))
                    {
                        onComplete(removed, failure);
                        yield break;
                    }

                    resetObjects.Add(resetObject.m_uid);
                }
            }
        }

        foreach (Vector2i zone in zones)
        {
            if (!CanContinueReset(zone, canResetZone, out failure))
            {
                onComplete(removed, failure);
                yield break;
            }
        }

        processedSinceYield = 0;
        foreach (ZDOID resetId in resetObjects)
        {
            if (processedSinceYield >= ResetBatchSize)
            {
                DataHelper.FlushDestroyed();
                processedSinceYield = 0;
                yield return null;
                foreach (Vector2i zone in zones)
                {
                    if (!CanContinueReset(zone, canResetZone, out failure))
                    {
                        onComplete(removed, failure);
                        yield break;
                    }
                }

                protectedCharacterIds.UnionWith(GetOnlineCharacterIds());
            }

            processedSinceYield++;
            // ZDO instances are pooled: retain IDs, then resolve again after any yield/deletion.
            ZDO? resetObject = ZDOMan.instance.GetZDO(resetId);
            if (resetObject == null || !resetObject.IsValid() || protectedCharacterIds.Contains(resetId))
            {
                continue;
            }

            if (!DataHelper.TryDestroyWithinZones(resetObject, zones, out int destroyed, out failure, destroyedIds, beforeMutation))
            {
                onComplete(removed, failure);
                yield break;
            }

            removed += destroyed;
        }

        DataHelper.FlushDestroyed();
        onComplete(removed, null);
    }

    private static IEnumerator CountRemainingCreatorWearNTearAsync(HashSet<Vector2i> zones, HashSet<ZDOID> protectedCharacterIds,
        Func<Vector2i, bool>? canResetZone, Action<int, string?> onComplete)
    {
        HashSet<ZDOID> seen = [];
        List<ZDO> zoneObjects = [];
        int count = 0;
        int processedSinceYield = 0;
        foreach (Vector2i zone in zones)
        {
            if (!CanContinueReset(zone, canResetZone, out string? failure))
            {
                onComplete(count, failure);
                yield break;
            }

            protectedCharacterIds.UnionWith(GetOnlineCharacterIds());
            zoneObjects.Clear();
            ZoneSaviorZones.FindObjects(zone, zoneObjects);
            foreach (ZDO zdo in zoneObjects)
            {
                processedSinceYield++;
                if (processedSinceYield >= ResetBatchSize)
                {
                    processedSinceYield = 0;
                    yield return null;
                    if (!CanContinueReset(zone, canResetZone, out failure))
                    {
                        onComplete(count, failure);
                        yield break;
                    }

                    protectedCharacterIds.UnionWith(GetOnlineCharacterIds());
                }

                if (!TryCollectResetZoneObject(zdo, zones, seen, out ZDO resetObject) ||
                    protectedCharacterIds.Contains(resetObject.m_uid) ||
                    !IsCreatorWearNTear(resetObject))
                {
                    continue;
                }

                count++;
            }
        }

        onComplete(count, null);
    }

    private static bool TryCollectResetZoneObject(ZDO zdo, HashSet<Vector2i> zones, HashSet<ZDOID> seen, out ZDO resetObject)
    {
        resetObject = null!;
        if (zdo == null ||
            !zdo.IsValid() ||
            !seen.Add(zdo.m_uid) ||
            !zones.Contains(ZoneSaviorZones.GetZone(zdo.GetPosition())))
        {
            return false;
        }

        resetObject = zdo;
        return true;
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

        if (!ZNet.instance.LocalPlayerCharacterID.IsNone())
        {
            ids.Add(ZNet.instance.LocalPlayerCharacterID);
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
        return Heightmap.GetAllHeightmaps()
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
            ZoneSaviorGameAccess.BuildData(heightmap) = null!;
            heightmap.Poke(delayed: 1, paintOnly: false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to recalculate loaded terrain heightmap: {ex.Message}");
            return false;
        }
    }
}

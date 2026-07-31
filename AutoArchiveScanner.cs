using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace ZoneSavior;

internal static class AutoArchiveScanner
{
    private static readonly Vector2i[] NeighborOffsets =
    [
        new(-1, -1),
        new(0, -1),
        new(1, -1),
        new(-1, 0),
        new(1, 0),
        new(-1, 1),
        new(0, 1),
        new(1, 1)
    ];

    private static ManualLogSource _logger = null!;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    public static IEnumerator Run(AutoArchiveScanOptions options, Action<ArchiveRunRecord> onComplete)
    {
        DateTime utcNow = DateTime.UtcNow;
        ArchiveRunRecord run = new()
        {
            RunId = utcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture),
            CreatedUtc = utcNow,
            Manual = options.Manual,
            DryRun = options.DryRun,
            ResetAfterSave = options.ResetAfterSave,
            TargetPlayerIds = options.TargetPlayerIds.ToList()
        };

        if (!IsWorldReady())
        {
            run.Messages.Add("World is not ready.");
            onComplete(run);
            yield break;
        }

        ZoneSaviorBuildRecipeRules.RefreshIndex();
        PlayerActivityTracker.TrackOnlinePlayers(utcNow);

        Dictionary<Vector2i, AutoArchiveZoneInfo> zoneInfos = [];
        yield return ScanZdosBySector(utcNow, zoneInfos, run);

        HashSet<long> targetPlayerIds = options.TargetPlayerIds.ToHashSet();
        bool isTargetOverride = targetPlayerIds.Count > 0;
        Dictionary<Vector2i, AutoArchiveZoneInfo> candidates = BuildCandidateZones(
            zoneInfos,
            utcNow,
            targetPlayerIds,
            options.ResetAfterSave,
            run);
        run.CandidateZones = candidates.Count;

        List<List<Vector2i>> clusters = BuildClusters(candidates);
        int processedZones = 0;
        int clusterIndex = 0;

        foreach (List<Vector2i> cluster in clusters.OrderBy(cluster => cluster.Min(zone => zone.x)).ThenBy(cluster => cluster.Min(zone => zone.y)))
        {
            clusterIndex++;
            ArchiveClusterRecord record = BuildClusterRecord(cluster, candidates);
            bool smallCluster = !isTargetOverride &&
                                record.PieceCount < AutoArchiveConfig.MinimumPiecesPerCluster;

            if (smallCluster)
            {
                if (!options.ResetAfterSave)
                {
                    record.Status = "skipped";
                    record.Reason = $"piece count {record.PieceCount} is below minimum {AutoArchiveConfig.MinimumPiecesPerCluster}; reset mode is not enabled";
                    run.Clusters.Add(record);
                    continue;
                }
            }

            // The scan yields between clusters. Recheck immediately before reserving
            // work so a creator who came online during an earlier cluster is protected.
            if (!isTargetOverride &&
                !AllCreatorsEligible(record.Creators, DateTime.UtcNow, out string creatorReason))
            {
                record.Status = "skipped";
                record.Reason = creatorReason;
                run.Clusters.Add(record);
                continue;
            }

            if (processedZones + cluster.Count > AutoArchiveConfig.MaxZonesPerRun)
            {
                record.Status = "skipped";
                record.Reason = $"max zones per run would be exceeded ({processedZones + cluster.Count}/{AutoArchiveConfig.MaxZonesPerRun})";
                run.Clusters.Add(record);
                continue;
            }

            // MaxZonesPerRun is a work reservation cap. Count an accepted cluster
            // before starting it so failed save/reset attempts cannot bypass the cap.
            processedZones += cluster.Count;

            if (smallCluster)
            {
                if (options.DryRun)
                {
                    record.Status = "dry-run-reset-without-save";
                    record.Reason = $"small inactive cluster would be reset without saving ({record.PieceCount}/{AutoArchiveConfig.MinimumPiecesPerCluster} pieces)";
                    run.Clusters.Add(record);
                    continue;
                }

                ZoneBundleResetResult? resetOnlyResult = null;
                yield return ZoneBundleCommands.ResetGeneratedZonesAsync(cluster, result => resetOnlyResult = result);
                if (resetOnlyResult == null)
                {
                    record.Status = "reset-without-save-failed";
                    record.Reason = "reset failed: reset coroutine did not return a result";
                    run.Clusters.Add(record);
                    _logger.LogError("Auto archive small-cluster reset failed: reset coroutine did not return a result.");
                    yield return null;
                    continue;
                }

                record.Status = resetOnlyResult.Success ? "reset-without-save" : "reset-without-save-failed";
                record.Reason = resetOnlyResult.Message;

                run.Clusters.Add(record);
                yield return null;
                continue;
            }

            record.Tag = ZoneBundleCommands.MakeUniqueAutoArchiveTag(BuildTag(clusterIndex, cluster, candidates));
            if (options.DryRun)
            {
                record.Status = "dry-run";
                record.Reason = isTargetOverride
                    ? "target override candidate; dry run is enabled"
                    : "candidate only; dry run is enabled";
                run.Clusters.Add(record);
                continue;
            }

            ZoneBundleArchiveResult? saveResult = null;
            yield return ZoneBundleCommands.SaveZonesAsync(cluster, record.Tag, result => saveResult = result);
            if (saveResult == null)
            {
                record.Status = "failed";
                record.Reason = "save failed: archive coroutine did not return a result";
                run.Clusters.Add(record);
                _logger.LogError($"Auto archive save failed for tag '{record.Tag}': archive coroutine did not return a result.");
                yield return null;
                continue;
            }

            record.TerrainLoaded = saveResult.TerrainLoaded;
            record.TerrainCaptured = saveResult.TerrainCaptured;
            if (!saveResult.Success)
            {
                record.Status = "failed";
                record.Reason = saveResult.Message;
                run.Clusters.Add(record);
                continue;
            }

            record.Status = "saved";
            record.Reason = saveResult.Message;

            if (options.ResetAfterSave)
            {
                ZoneBundleResetResult? resetResult = null;
                yield return ZoneBundleCommands.ResetGeneratedZonesAsync(cluster, result => resetResult = result);
                if (resetResult == null)
                {
                    record.Status = "saved-reset-failed";
                    record.Reason = "reset failed: reset coroutine did not return a result";
                    run.Clusters.Add(record);
                    _logger.LogError($"Auto archive reset failed for tag '{record.Tag}': reset coroutine did not return a result.");
                    yield return null;
                    continue;
                }

                record.Status = resetResult.Success ? "reset" : "saved-reset-failed";
                record.Reason = resetResult.Message;
            }

            run.Clusters.Add(record);
            yield return null;
        }

        run.ProcessedZones = processedZones;
        onComplete(run);
    }

    private static bool TryReadPlayerStructure(ZDO zdo, DateTime utcNow, out Vector2i zone, out long creator)
    {
        zone = default;
        creator = 0L;

        if (!ZoneStructureClassifier.TryGetAutoArchiveCandidate(
                zdo,
                out zone,
                out creator,
                out string creatorName))
        {
            return false;
        }

        AutoArchiveStore.RecordUnknownPlayer(creator, utcNow, creatorName);

        return true;
    }

    private static IEnumerator ScanZdosBySector(
        DateTime utcNow,
        Dictionary<Vector2i, AutoArchiveZoneInfo> zoneInfos,
        ArchiveRunRecord run)
    {
        int batchSize = Math.Max(1, AutoArchiveConfig.ScannerBatchSize);
        int processedSinceYield = 0;
        List<ZDO>[] sectors = ZDOMan.instance.m_objectsBySector;
        for (int sectorIndex = 0; sectors != null && sectorIndex < sectors.Length; sectorIndex++)
        {
            List<ZDO> sector = sectors[sectorIndex];
            if (sector == null || sector.Count == 0)
            {
                continue;
            }

            for (int objectIndex = 0; objectIndex < sector.Count; objectIndex++)
            {
                ProcessScanZdo(sector[objectIndex], utcNow, zoneInfos, run);
                processedSinceYield++;
                if (processedSinceYield >= batchSize)
                {
                    processedSinceYield = 0;
                    yield return null;
                }
            }
        }

        List<List<ZDO>> outsideSectors = ZDOMan.instance.m_objectsByOutsideSector?.Values.ToList() ?? [];
        foreach (List<ZDO> sector in outsideSectors)
        {
            if (sector == null || sector.Count == 0)
            {
                continue;
            }

            for (int objectIndex = 0; objectIndex < sector.Count; objectIndex++)
            {
                ProcessScanZdo(sector[objectIndex], utcNow, zoneInfos, run);
                processedSinceYield++;
                if (processedSinceYield >= batchSize)
                {
                    processedSinceYield = 0;
                    yield return null;
                }
            }
        }
    }

    private static void ProcessScanZdo(
        ZDO zdo,
        DateTime utcNow,
        Dictionary<Vector2i, AutoArchiveZoneInfo> zoneInfos,
        ArchiveRunRecord run)
    {
        run.ScannedZdos++;
        if (!TryReadPlayerStructure(zdo, utcNow, out Vector2i zone, out long creator))
        {
            return;
        }

        run.StructureZdos++;
        if (!zoneInfos.TryGetValue(zone, out AutoArchiveZoneInfo info))
        {
            info = new AutoArchiveZoneInfo(zone);
            zoneInfos[zone] = info;
        }

        info.AddCreator(creator);
    }

    private static Dictionary<Vector2i, AutoArchiveZoneInfo> BuildCandidateZones(
        Dictionary<Vector2i, AutoArchiveZoneInfo> zoneInfos,
        DateTime utcNow,
        HashSet<long> targetPlayerIds,
        bool resetAfterSave,
        ArchiveRunRecord run)
    {
        Dictionary<Vector2i, AutoArchiveZoneInfo> candidates = [];
        bool isTargetOverride = targetPlayerIds.Count > 0;
        foreach (AutoArchiveZoneInfo info in zoneInfos.Values)
        {
            if (isTargetOverride)
            {
                if (!info.Creators.Any(targetPlayerIds.Contains))
                {
                    continue;
                }

                if (resetAfterSave &&
                    HasNonTargetCreators(info.Creators, targetPlayerIds, out List<long> nonTargetCreators))
                {
                    run.Messages.Add(
                        $"Skipped zone ({info.Zone.x},{info.Zone.y}): target reset blocked for mixed-owner zone; non-target creator(s): {FormatCreatorList(nonTargetCreators)}");
                    continue;
                }

                candidates[info.Zone] = info;
                continue;
            }

            if (!AllCreatorsEligible(info.Creators, utcNow, out string reason))
            {
                run.Messages.Add($"Skipped zone ({info.Zone.x},{info.Zone.y}): {reason}");
                continue;
            }

            candidates[info.Zone] = info;
        }

        return candidates;
    }

    private static bool HasNonTargetCreators(IEnumerable<long> creators, HashSet<long> targetPlayerIds, out List<long> nonTargetCreators)
    {
        nonTargetCreators = creators
            .Where(creator => creator != 0L && !targetPlayerIds.Contains(creator))
            .Distinct()
            .OrderBy(creator => creator)
            .ToList();
        return nonTargetCreators.Count > 0;
    }

    private static string FormatCreatorList(IEnumerable<long> creators)
    {
        return string.Join(", ", creators.Select(creator => creator.ToString(CultureInfo.InvariantCulture)));
    }

    private static List<List<Vector2i>> BuildClusters(Dictionary<Vector2i, AutoArchiveZoneInfo> candidates)
    {
        List<List<Vector2i>> clusters = [];
        HashSet<Vector2i> remaining = candidates.Keys.ToHashSet();

        while (remaining.Count > 0)
        {
            Vector2i start = remaining.First();
            remaining.Remove(start);

            List<Vector2i> cluster = [];
            Queue<Vector2i> queue = new();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                Vector2i zone = queue.Dequeue();
                cluster.Add(zone);

                foreach (Vector2i offset in NeighborOffsets)
                {
                    Vector2i neighbor = new(zone.x + offset.x, zone.y + offset.y);
                    if (!remaining.Contains(neighbor))
                    {
                        continue;
                    }

                    remaining.Remove(neighbor);
                    queue.Enqueue(neighbor);
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static ArchiveClusterRecord BuildClusterRecord(List<Vector2i> cluster, Dictionary<Vector2i, AutoArchiveZoneInfo> candidates)
    {
        ArchiveClusterRecord record = new()
        {
            Zones = cluster
                .OrderBy(zone => zone.y)
                .ThenBy(zone => zone.x)
                .Select(ZoneSaviorZones.ToModel)
                .ToList()
        };

        HashSet<long> creators = [];
        foreach (Vector2i zone in cluster)
        {
            AutoArchiveZoneInfo info = candidates[zone];
            record.PieceCount += info.PieceCount;
            foreach (long creator in info.Creators)
            {
                creators.Add(creator);
            }
        }

        record.Creators = creators.OrderBy(id => id).ToList();
        return record;
    }

    private static bool AllCreatorsEligible(IEnumerable<long> creators, DateTime utcNow, out string reason)
    {
        foreach (long creator in creators)
        {
            if (!AutoArchiveStore.IsCreatorArchiveEligible(
                    creator,
                    utcNow,
                    AutoArchiveConfig.InactiveDays,
                    out reason))
            {
                return false;
            }
        }

        reason = "";
        return true;
    }

    private static string BuildTag(int index, List<Vector2i> cluster, Dictionary<Vector2i, AutoArchiveZoneInfo> candidates)
    {
        Dictionary<long, int> creatorPieceCounts = BuildCreatorPieceCounts(cluster, candidates);
        string ownerSegment = BuildOwnerSegment(creatorPieceCounts);
        return $"auto_{ownerSegment}_c{index:D3}";
    }

    private static Dictionary<long, int> BuildCreatorPieceCounts(List<Vector2i> cluster, Dictionary<Vector2i, AutoArchiveZoneInfo> candidates)
    {
        Dictionary<long, int> counts = [];
        foreach (Vector2i zone in cluster)
        {
            foreach (KeyValuePair<long, int> pair in candidates[zone].CreatorPieceCounts)
            {
                counts.TryGetValue(pair.Key, out int existing);
                counts[pair.Key] = existing + pair.Value;
            }
        }

        return counts;
    }

    private static string BuildOwnerSegment(IReadOnlyDictionary<long, int> creatorPieceCounts)
    {
        List<long> ownerIds = creatorPieceCounts.Keys
            .Where(creator => creator != 0L)
            .Distinct()
            .OrderBy(creator => creator)
            .ToList();

        if (ownerIds.Count == 0)
        {
            return "unknown";
        }

        long representative = ownerIds
            .OrderByDescending(owner => creatorPieceCounts.TryGetValue(owner, out int count) ? count : 0)
            .ThenBy(owner => owner)
            .First();

        string representativeToken = BuildOwnerToken(representative);
        if (ownerIds.Count == 1)
        {
            return representativeToken;
        }

        return $"{representativeToken}_plus{ownerIds.Count - 1}_{BuildOwnerHash(ownerIds)}";
    }

    private static string BuildOwnerToken(long playerId)
    {
        string name = ResolvePlayerName(playerId);
        string steamId = ResolveSteamId(playerId);
        if (!string.Equals(steamId, "unknown", StringComparison.Ordinal))
        {
            return $"{name}_s{steamId}";
        }

        return name;
    }

    private static string ResolvePlayerName(long playerId)
    {
        if (!AutoArchiveStore.TryGetPlayerRecord(playerId, out PlayerActivityRecord record))
        {
            return "unknown";
        }

        string name = record.Names
            .LastOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate) &&
                !string.Equals(candidate, "unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate, "manual", StringComparison.OrdinalIgnoreCase)) ?? "unknown";

        return ZoneSaviorPaths.SanitizeTagToken(name);
    }

    private static string ResolveSteamId(long playerId)
    {
        if (!AutoArchiveStore.TryGetPlayerRecord(playerId, out PlayerActivityRecord record) ||
            string.IsNullOrWhiteSpace(record.PlatformId) ||
            !record.PlatformId.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
        {
            return "unknown";
        }

        string digits = ZoneSaviorSteamIds.Normalize(record.PlatformId);
        if (!string.IsNullOrWhiteSpace(digits))
        {
            return digits;
        }

        string raw = record.PlatformId.Substring("steam:".Length);
        string sanitized = new(raw
            .Where(character => char.IsLetterOrDigit(character) || character == '-' || character == '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static string BuildOwnerHash(IEnumerable<long> ownerIds)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (long ownerId in ownerIds)
            {
                foreach (byte value in BitConverter.GetBytes(ownerId))
                {
                    hash ^= value;
                    hash *= 16777619u;
                }
            }

            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    private static bool IsWorldReady()
    {
        return ZNet.instance != null &&
               ZNet.instance.IsServer() &&
               ZDOMan.instance != null &&
               ZNetScene.instance != null &&
               ZoneSystem.instance != null;
    }

    private sealed class AutoArchiveZoneInfo
    {
        public AutoArchiveZoneInfo(Vector2i zone)
        {
            Zone = zone;
        }

        public Vector2i Zone { get; }
        public int PieceCount { get; set; }
        public HashSet<long> Creators { get; } = [];
        public Dictionary<long, int> CreatorPieceCounts { get; } = [];

        public void AddCreator(long creator)
        {
            PieceCount++;
            Creators.Add(creator);
            CreatorPieceCounts.TryGetValue(creator, out int count);
            CreatorPieceCounts[creator] = count + 1;
        }
    }
}


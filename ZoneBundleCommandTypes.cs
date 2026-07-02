using System;
using System.Collections.Generic;
using System.Linq;

namespace ZoneSavior;

internal static partial class ZoneBundleCommands
{
    private readonly struct ZoneLoadStats
    {
        public ZoneLoadStats(int removed, int created, bool terrainApplied)
        {
            Removed = removed;
            Created = created;
            TerrainApplied = terrainApplied;
        }

        public int Removed { get; }
        public int Created { get; }
        public bool TerrainApplied { get; }
    }

    private struct ZoneLoadTotals
    {
        public int Removed { get; private set; }
        public int Created { get; private set; }
        public int TerrainApplied { get; private set; }

        public void Add(ZoneLoadStats stats, bool terrainApplied)
        {
            Removed += stats.Removed;
            Created += stats.Created;
            if (terrainApplied)
            {
                TerrainApplied++;
            }
        }
    }

    private sealed class ZoneLoadIssueSummary
    {
        private readonly Dictionary<string, int> _missingPrefabs = new(StringComparer.Ordinal);

        public void AddMissingPrefab(string prefab)
        {
            if (_missingPrefabs.TryGetValue(prefab, out int count))
            {
                _missingPrefabs[prefab] = count + 1;
                return;
            }

            _missingPrefabs[prefab] = 1;
        }

        public void Log(Vector2i targetZone, string tag)
        {
            if (_missingPrefabs.Count == 0)
            {
                return;
            }

            int total = _missingPrefabs.Values.Sum();
            string sample = string.Join(
                ", ",
                _missingPrefabs
                    .OrderByDescending(item => item.Value)
                    .ThenBy(item => item.Key, StringComparer.Ordinal)
                    .Take(8)
                    .Select(item => item.Value == 1 ? item.Key : $"{item.Key} x{item.Value}"));
            int extra = Math.Max(0, _missingPrefabs.Count - 8);
            if (extra > 0)
            {
                sample += $", +{extra} more";
            }

            _logger.LogWarning(
                $"Skipped {total} missing prefab entr{(total == 1 ? "y" : "ies")} while loading zone bundle '{tag}' " +
                $"into ({targetZone.x},{targetZone.y}): {sample}.");
        }
    }

    private readonly struct TerrainPreparationResult
    {
        private TerrainPreparationResult(bool success, string message, HashSet<Vector2i> clientPreparedZones, int preparedZones, int changedZones)
        {
            Success = success;
            Message = message;
            ClientPreparedZones = clientPreparedZones;
            PreparedZones = preparedZones;
            ChangedZones = changedZones;
        }

        public bool Success { get; }
        public string Message { get; }
        private HashSet<Vector2i> ClientPreparedZones { get; }
        public int PreparedZones { get; }
        public int ChangedZones { get; }

        public bool WasClientPrepared(Vector2i zone)
        {
            return ClientPreparedZones.Contains(zone);
        }

        public string Describe()
        {
            return ClientPreparedZones.Count > 0 ? $", terrainBy: witness-client {ChangedZones}/{PreparedZones} prepared" : "";
        }

        public static TerrainPreparationResult Completed(bool clientAssisted, int preparedZones, int changedZones)
        {
            return new TerrainPreparationResult(true, "", clientAssisted ? [] : [], preparedZones, changedZones);
        }

        public static TerrainPreparationResult Completed(HashSet<Vector2i> clientPreparedZones, int preparedZones, int changedZones)
        {
            return new TerrainPreparationResult(true, "", clientPreparedZones, preparedZones, changedZones);
        }

        public static TerrainPreparationResult Failed(string message)
        {
            return new TerrainPreparationResult(false, message, [], 0, 0);
        }
    }

    private readonly struct TerrainWitnessCandidate
    {
        public TerrainWitnessCandidate(long peerId, float distanceSqr, bool preferred)
        {
            PeerId = peerId;
            DistanceSqr = distanceSqr;
            Preferred = preferred;
        }

        public long PeerId { get; }
        public float DistanceSqr { get; }
        public bool Preferred { get; }
    }

    private sealed class CaptureBundleResult
    {
        private CaptureBundleResult()
        {
        }

        public bool Success { get; private set; }
        public string ErrorMessage { get; private set; } = "";
        public ZoneBundleFile? Bundle { get; private set; }
        public int EntryCount { get; private set; }
        public int MonsterCount { get; private set; }
        public ZoneBundleTerrainCaptureState TerrainState { get; private set; }

        public static CaptureBundleResult Completed(ZoneBundleFile bundle, int entryCount, int monsterCount, ZoneBundleTerrainCaptureState terrainState)
        {
            return new CaptureBundleResult
            {
                Success = true,
                Bundle = bundle,
                EntryCount = entryCount,
                MonsterCount = monsterCount,
                TerrainState = terrainState
            };
        }

        public static CaptureBundleResult Failed(string errorMessage)
        {
            return new CaptureBundleResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }
    }

    private readonly struct LoadWorkItem
    {
        public LoadWorkItem(Vector2i targetZone, ZoneBundleFile bundle)
        {
            TargetZone = targetZone;
            Bundle = bundle;
        }

        public Vector2i TargetZone { get; }
        public ZoneBundleFile Bundle { get; }
    }

    private readonly struct ResetVerificationResult
    {
        public ResetVerificationResult(int removed, int remainingWearNTear)
        {
            Removed = removed;
            RemainingWearNTear = remainingWearNTear;
        }

        public int Removed { get; }
        public int RemainingWearNTear { get; }
    }
}

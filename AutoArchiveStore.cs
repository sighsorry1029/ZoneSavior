using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ZoneSavior;

internal static class AutoArchiveStore
{
    private const string UnknownPlatformPrefix = "unknown:";
    private const int MaxRunHistory = 50;
    private static readonly TimeSpan InternalWriteReloadGrace = TimeSpan.FromSeconds(2);

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly object Sync = new();

    private static ManualLogSource _logger = null!;
    private static bool _initialized;
    private static bool _dirty;
    private static DateTime _lastFlushUtc = DateTime.MinValue;
    private static DateTime _lastInternalWriteUtc = DateTime.MinValue;
    private static readonly Dictionary<string, PlayerActivityRecord> PlayersByPlatform = new(StringComparer.Ordinal);
    private static readonly Dictionary<long, PlayerActivityRecord> PlayersById = [];

    public static string FilePath => Path.Combine(ZoneSaviorPlugin.DataStorageFullPath, ZoneSaviorPlugin.ActivityFileName);
    public static AutoArchiveState State { get; private set; } = new();

    public static void Initialize(ManualLogSource logger)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _logger = logger;
        Load();
    }

    public static void Load()
    {
        lock (Sync)
        {
            if (!File.Exists(FilePath))
            {
                ReplaceState(new AutoArchiveState());
                _dirty = true;
                Flush(force: true);
                return;
            }

            try
            {
                if (!TryReadStateFromDisk(out AutoArchiveState state, out string error))
                {
                    throw new InvalidDataException(error);
                }

                ReplaceState(state);
                _dirty = false;
            }
            catch (Exception ex)
            {
                BackupInvalidActivityFile();
                _logger.LogError($"Failed to load auto archive activity file, using a blank state: {ex}");
                ReplaceState(new AutoArchiveState());
                _dirty = true;
            }
        }
    }

    private static void BackupInvalidActivityFile()
    {
        try
        {
            string directory = Path.GetDirectoryName(FilePath)!;
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string backupPath = Path.Combine(directory, $"activity.invalid.{timestamp}.yml");
            File.Copy(FilePath, backupPath, overwrite: false);
            _logger.LogWarning($"Backed up invalid auto archive activity data to '{backupPath}'.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to back up invalid auto archive activity data: {ex.Message}");
        }
    }

    public static void Flush(bool force = false)
    {
        lock (Sync)
        {
            if (!_dirty && !force)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!force && now - _lastFlushUtc < TimeSpan.FromSeconds(60))
            {
                return;
            }

            ZoneSaviorFiles.WriteAllTextAtomic(FilePath, Serializer.Serialize(State));
            _lastFlushUtc = now;
            _lastInternalWriteUtc = now;
            _dirty = false;
        }
    }

    public static bool TryReloadFromDiskIfSafe(bool scanRunning, out string message)
    {
        lock (Sync)
        {
            if (_dirty)
            {
                message = "Activity reload skipped: runtime activity state has unsaved changes.";
                return false;
            }

            if (scanRunning)
            {
                message = "Activity reload skipped: auto archive scan is running.";
                return false;
            }

            if (DateTime.UtcNow - _lastInternalWriteUtc < InternalWriteReloadGrace)
            {
                message = "Activity reload skipped: change was caused by a recent ZoneSavior activity flush.";
                return false;
            }

            if (!File.Exists(FilePath))
            {
                message = "Activity reload skipped: activity.yml does not exist.";
                return false;
            }

            if (!TryReadStateFromDisk(out AutoArchiveState state, out string error))
            {
                message = $"Activity reload skipped: {error}";
                return false;
            }

            ReplaceState(state);
            _dirty = false;
            message = "Activity reload complete.";
            return true;
        }
    }

    public static void RecordPlayerSeen(string platformId, long playerId, string name, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(platformId))
        {
            platformId = playerId != 0 ? UnknownPlatformId(playerId) : "unknown";
        }

        lock (Sync)
        {
            PlayerActivityRecord? record = PlayersByPlatform.TryGetValue(platformId, out PlayerActivityRecord indexed)
                ? indexed
                : null;
            if (record == null)
            {
                record = new PlayerActivityRecord
                {
                    PlatformId = platformId
                };
                State.Players.Add(record);
                IndexPlayer(record);
            }

            record.LastSeenUtc = utcNow;
            AddDistinct(record.Names, name);

            if (playerId != 0)
            {
                PlayerActivityRecord? duplicate = PlayersById.TryGetValue(playerId, out PlayerActivityRecord indexedById)
                    ? indexedById
                    : null;
                AddDistinct(record.PlayerIds, playerId);
                PlayersById[playerId] = record;
                if (duplicate != null && duplicate != record)
                {
                    MergePlayerRecords(record, duplicate);
                    RebuildPlayerIndexes();
                }
            }

            _dirty = true;
        }
    }

    public static void RecordUnknownPlayer(long playerId, DateTime utcNow, string observedName = "")
    {
        if (playerId == 0)
        {
            return;
        }

        lock (Sync)
        {
            if (PlayersById.TryGetValue(playerId, out PlayerActivityRecord existing))
            {
                if (existing.LastSeenUtc == DateTime.MinValue)
                {
                    existing.LastSeenUtc = utcNow;
                    _dirty = true;
                }

                if (AddObservedName(existing, observedName))
                {
                    _dirty = true;
                }

                return;
            }

            GetOrCreateUnknownPlayer(playerId, utcNow, observedName);
            _dirty = true;
        }
    }

    public static bool TryGetPlayerRecord(long playerId, out PlayerActivityRecord record)
    {
        lock (Sync)
        {
            record = PlayersById.TryGetValue(playerId, out PlayerActivityRecord indexed) ? indexed : null!;
            return record != null;
        }
    }

    internal static bool TryResolveLastKnownPlayerName(long playerId, out string name)
    {
        name = "";
        if (playerId == 0L)
        {
            return false;
        }

        lock (Sync)
        {
            if (!PlayersById.TryGetValue(playerId, out PlayerActivityRecord record) || record.Names == null)
            {
                return false;
            }

            for (int index = record.Names.Count - 1; index >= 0; index--)
            {
                string candidate = record.Names[index]?.Trim() ?? "";
                if (candidate.Length == 0 ||
                    string.Equals(candidate, "unknown", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate, "manual", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Return an immutable value while still holding the store lock instead of
                // exposing the mutable activity record or its aliases collection.
                name = candidate;
                return true;
            }
        }

        return false;
    }

    public static bool TryGetPlayerIdsBySteamId(string steamId, out List<long> playerIds, out string normalizedSteamId)
    {
        normalizedSteamId = ZoneSaviorSteamIds.Normalize(steamId);
        playerIds = [];
        if (string.IsNullOrWhiteSpace(normalizedSteamId))
        {
            return false;
        }

        lock (Sync)
        {
            foreach (PlayerActivityRecord record in State.Players)
            {
                if (!ZoneSaviorSteamIds.TryNormalizePlatformId(record.PlatformId, out string recordSteamId) ||
                    !string.Equals(recordSteamId, normalizedSteamId, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (long playerId in record.PlayerIds.Where(id => id != 0L))
                {
                    AddDistinct(playerIds, playerId);
                }
            }
        }

        return playerIds.Count > 0;
    }

    public static AutoArchiveCreatorEligibility EvaluateCreatorArchiveEligibility(
        long playerId,
        DateTime utcNow,
        int inactiveDays,
        bool recordUnknownPlayer,
        string observedName = "")
    {
        AutoArchiveCreatorEligibility result = new()
        {
            PlayerId = playerId
        };

        if (playerId == 0)
        {
            result.Reason = "creatorless";
            return result;
        }

        bool recordedInActivity = TryGetPlayerRecord(playerId, out PlayerActivityRecord record);
        string platformId = recordedInActivity ? record.PlatformId : "";
        IEnumerable<string> names = recordedInActivity ? record.Names.ToList() : [];
        if (ZoneLimitConfiguration.IsArchiveProtected(playerId, platformId, names, out string protectionReason))
        {
            result.Protected = true;
            result.Reason = protectionReason;
            return result;
        }

        if (!recordedInActivity)
        {
            result.PlatformId = UnknownPlatformId(playerId);
            result.Names = string.IsNullOrWhiteSpace(observedName) ? ["not_recorded_in_activity"] : [observedName.Trim()];
            result.UnknownActivityRecord = true;

            if (recordUnknownPlayer)
            {
                RecordUnknownPlayer(playerId, utcNow, observedName);
                result.Reason = $"player {playerId} was first discovered by scanner";
            }
            else
            {
                result.Reason = $"player {playerId} is not recorded in activity";
            }

            return result;
        }

        result.RecordedInActivity = true;
        result.PlatformId = record.PlatformId;
        result.Names = record.Names?.ToList() ?? [];

        bool unknown = IsUnknown(record);
        result.UnknownActivityRecord = unknown;
        DateTime reference = record.LastSeenUtc;

        if (reference == DateTime.MinValue)
        {
            result.Reason = $"player {playerId} has no usable activity timestamp";
            return result;
        }

        TimeSpan age = utcNow - DateTime.SpecifyKind(reference, DateTimeKind.Utc);
        if (age.TotalDays < inactiveDays)
        {
            result.Reason = unknown
                ? $"unknown player {playerId} discovered {age.TotalDays:F1}/{inactiveDays} days ago"
                : $"player {playerId} last seen {age.TotalDays:F1}/{inactiveDays} days ago";
            return result;
        }

        result.Eligible = true;
        result.Reason = unknown
            ? $"unknown player {playerId} inactive since discovery for {age.TotalDays:F1} days"
            : $"player {playerId} inactive for {age.TotalDays:F1} days";
        return result;
    }

    public static bool IsCreatorArchiveEligible(long playerId, DateTime utcNow, int inactiveDays, out string reason)
    {
        AutoArchiveCreatorEligibility evaluation = EvaluateCreatorArchiveEligibility(
            playerId,
            utcNow,
            inactiveDays,
            recordUnknownPlayer: true);
        reason = evaluation.Reason;
        return evaluation.Eligible;
    }

    public static void RecordRun(ArchiveRunRecord run)
    {
        lock (Sync)
        {
            State.LastScanUtc = run.CreatedUtc;
            if (!run.Manual)
            {
                State.LastAutoScanUtc = run.CreatedUtc;
            }

            State.Runs.Add(run);
            if (State.Runs.Count > MaxRunHistory)
            {
                State.Runs.RemoveRange(0, State.Runs.Count - MaxRunHistory);
            }

            _dirty = true;
        }
    }

    private static PlayerActivityRecord GetOrCreateUnknownPlayer(long playerId, DateTime utcNow, string observedName = "")
    {
        string platformId = UnknownPlatformId(playerId);
        if (PlayersByPlatform.TryGetValue(platformId, out PlayerActivityRecord record))
        {
            AddObservedName(record, observedName);
            return record;
        }

        List<string> names = [];
        AddObservedName(names, observedName);
        if (names.Count == 0)
        {
            names.Add("unknown");
        }

        record = new PlayerActivityRecord
        {
            PlatformId = platformId,
            LastSeenUtc = utcNow,
            PlayerIds = [playerId],
            Names = names
        };
        State.Players.Add(record);
        IndexPlayer(record);
        return record;
    }

    private static bool AddObservedName(PlayerActivityRecord record, string observedName)
    {
        return AddObservedName(record.Names, observedName);
    }

    private static bool AddObservedName(List<string> names, string observedName)
    {
        if (string.IsNullOrWhiteSpace(observedName) ||
            string.Equals(observedName, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string trimmed = observedName.Trim();
        if (names.Contains(trimmed))
        {
            return false;
        }

        names.Add(trimmed);
        return true;
    }

    private static void MergePlayerRecords(PlayerActivityRecord target, PlayerActivityRecord duplicate)
    {
        foreach (long id in duplicate.PlayerIds)
        {
            AddDistinct(target.PlayerIds, id);
        }

        foreach (string name in duplicate.Names)
        {
            AddDistinct(target.Names, name);
        }

        if (duplicate.LastSeenUtc > target.LastSeenUtc)
        {
            target.LastSeenUtc = duplicate.LastSeenUtc;
        }

        State.Players.Remove(duplicate);
    }

    private static void RebuildPlayerIndexes()
    {
        BuildPlayerIndexes(
            State,
            out Dictionary<string, PlayerActivityRecord> playersByPlatform,
            out Dictionary<long, PlayerActivityRecord> playersById);
        ReplaceIndexes(playersByPlatform, playersById);
    }

    private static void ReplaceState(AutoArchiveState state)
    {
        NormalizePlayerRecords(state);
        BuildPlayerIndexes(
            state,
            out Dictionary<string, PlayerActivityRecord> playersByPlatform,
            out Dictionary<long, PlayerActivityRecord> playersById);
        State = state;
        ReplaceIndexes(playersByPlatform, playersById);
    }

    private static void NormalizePlayerRecords(AutoArchiveState state)
    {
        bool merged;
        do
        {
            merged = false;
            for (int targetIndex = 0; targetIndex < state.Players.Count && !merged; targetIndex++)
            {
                PlayerActivityRecord target = state.Players[targetIndex];
                for (int duplicateIndex = targetIndex + 1; duplicateIndex < state.Players.Count; duplicateIndex++)
                {
                    PlayerActivityRecord duplicate = state.Players[duplicateIndex];
                    if (!target.PlayerIds.Any(id => id != 0L && duplicate.PlayerIds.Contains(id)))
                    {
                        continue;
                    }

                    bool preferDuplicate = GetPlatformIdentityPriority(duplicate) >
                                           GetPlatformIdentityPriority(target);
                    PlayerActivityRecord canonical = preferDuplicate ? duplicate : target;
                    PlayerActivityRecord other = preferDuplicate ? target : duplicate;
                    foreach (long id in other.PlayerIds)
                    {
                        AddDistinct(canonical.PlayerIds, id);
                    }

                    foreach (string name in other.Names)
                    {
                        AddDistinct(canonical.Names, name);
                    }

                    if (other.LastSeenUtc > canonical.LastSeenUtc)
                    {
                        canonical.LastSeenUtc = other.LastSeenUtc;
                    }

                    state.Players.RemoveAt(preferDuplicate ? targetIndex : duplicateIndex);
                    merged = true;
                    break;
                }
            }
        } while (merged);
    }

    private static void BuildPlayerIndexes(
        AutoArchiveState state,
        out Dictionary<string, PlayerActivityRecord> playersByPlatform,
        out Dictionary<long, PlayerActivityRecord> playersById)
    {
        playersByPlatform = new Dictionary<string, PlayerActivityRecord>(StringComparer.Ordinal);
        playersById = [];
        foreach (PlayerActivityRecord player in state.Players)
        {
            if (!string.IsNullOrWhiteSpace(player.PlatformId))
            {
                playersByPlatform[player.PlatformId] = player;
            }

            foreach (long playerId in player.PlayerIds)
            {
                if (playerId != 0L)
                {
                    playersById[playerId] = player;
                }
            }
        }
    }

    private static void ReplaceIndexes(
        Dictionary<string, PlayerActivityRecord> playersByPlatform,
        Dictionary<long, PlayerActivityRecord> playersById)
    {
        PlayersByPlatform.Clear();
        foreach (KeyValuePair<string, PlayerActivityRecord> pair in playersByPlatform)
        {
            PlayersByPlatform[pair.Key] = pair.Value;
        }

        PlayersById.Clear();
        foreach (KeyValuePair<long, PlayerActivityRecord> pair in playersById)
        {
            PlayersById[pair.Key] = pair.Value;
        }
    }

    private static void IndexPlayer(PlayerActivityRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.PlatformId))
        {
            PlayersByPlatform[record.PlatformId] = record;
        }

        foreach (long playerId in record.PlayerIds)
        {
            if (playerId != 0L)
            {
                PlayersById[playerId] = record;
            }
        }
    }

    private static string UnknownPlatformId(long playerId)
    {
        return UnknownPlatformPrefix + playerId;
    }

    private static bool IsUnknown(PlayerActivityRecord record)
    {
        return record.PlatformId.StartsWith(UnknownPlatformPrefix, StringComparison.Ordinal);
    }

    private static int GetPlatformIdentityPriority(PlayerActivityRecord record)
    {
        if (ZoneSaviorSteamIds.TryNormalizePlatformId(record.PlatformId, out _))
        {
            return 2;
        }

        return string.IsNullOrWhiteSpace(record.PlatformId) || IsUnknown(record) ? 0 : 1;
    }

    private static bool TryReadStateFromDisk(out AutoArchiveState state, out string error)
    {
        state = new AutoArchiveState();
        error = "";
        try
        {
            string yaml = File.ReadAllText(FilePath);
            state = Deserializer.Deserialize<AutoArchiveState>(yaml) ?? new AutoArchiveState();
            ValidateState(state);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ValidateState(AutoArchiveState state)
    {
        if (state.Version != AutoArchiveState.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported activity version {state.Version}.");
        }

        if (state.Players == null || state.Runs == null)
        {
            throw new InvalidDataException("Activity players or runs list is missing.");
        }

        for (int index = 0; index < state.Players.Count; index++)
        {
            PlayerActivityRecord? player = state.Players[index];
            if (player == null || player.PlatformId == null || player.PlayerIds == null || player.Names == null)
            {
                throw new InvalidDataException($"Activity player record {index} is invalid.");
            }
        }

        for (int runIndex = 0; runIndex < state.Runs.Count; runIndex++)
        {
            ArchiveRunRecord? run = state.Runs[runIndex];
            if (run == null || run.TargetPlayerIds == null || run.Clusters == null || run.Messages == null)
            {
                throw new InvalidDataException($"Activity run record {runIndex} is invalid.");
            }

            for (int clusterIndex = 0; clusterIndex < run.Clusters.Count; clusterIndex++)
            {
                ArchiveClusterRecord? cluster = run.Clusters[clusterIndex];
                if (cluster == null || cluster.Creators == null || cluster.Zones == null || cluster.Zones.Any(zone => zone == null))
                {
                    throw new InvalidDataException($"Activity run {runIndex} cluster {clusterIndex} is invalid.");
                }
            }
        }
    }

    private static void AddDistinct<T>(List<T> values, T value)
    {
        if (value == null || values.Contains(value))
        {
            return;
        }

        values.Add(value);
    }
}


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using ServerSync;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ZoneSavior;

internal static class ZoneLimitConfiguration
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly List<ZoneLimitRule> Rules = [];
    private static readonly HashSet<long> ProtectedPlayerIds = [];
    private static readonly HashSet<string> ProtectedSteamIds = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ProtectedPlayerNames = new(StringComparer.OrdinalIgnoreCase);

    private static ManualLogSource? _logger;
    private static CustomSyncedValue<string>? _syncedYaml;

    public static bool Enabled => GeneralConfig.ZoneWearNTearLimitEnabled;

    public static void Initialize(ConfigSync configSync, ManualLogSource logger)
    {
        _logger = logger;
        _syncedYaml = new CustomSyncedValue<string>(configSync, "zone_rules_yaml", "");
        _syncedYaml.ValueChanged += OnSyncedYamlChanged;
        configSync.SourceOfTruthChanged += OnSourceOfTruthChanged;

        EnsureFileExists();
        ReloadFromDisk();
    }

    public static void ReloadFromDisk()
    {
        if (_syncedYaml == null)
        {
            return;
        }

        EnsureFileExists();

        if (!ZoneSaviorPlugin.ConfigSync.IsSourceOfTruth)
        {
            if (!string.IsNullOrWhiteSpace(_syncedYaml.Value))
            {
                ApplyYaml(_syncedYaml.Value, "server sync");
            }

            return;
        }

        string yaml = File.ReadAllText(ZoneSaviorPlugin.ZoneRuleFileFullPath);
        if (!TryParseYaml(yaml, out _, out _, out string error))
        {
            _logger?.LogError($"Zone rule YAML is invalid, keeping previous rules: {error}");
            return;
        }

        _syncedYaml.AssignLocalValue(yaml);
        if (string.IsNullOrWhiteSpace(_syncedYaml.Value))
        {
            ApplyConfiguration([], default);
        }
    }

    public static bool TryGetRule(Vector2i zone, out ZoneLimitRule rule)
    {
        if (!Enabled)
        {
            rule = default;
            return false;
        }

        foreach (ZoneLimitRule candidate in Rules)
        {
            if (candidate.Contains(zone))
            {
                rule = candidate;
                return true;
            }
        }

        rule = default;
        return false;
    }

    public static bool IsArchiveProtected(long playerId, out string reason)
    {
        reason = "";
        if (playerId == 0)
        {
            return false;
        }

        if (ProtectedPlayerIds.Contains(playerId))
        {
            reason = $"player {playerId} is protected by zones.yml player_ids";
            return true;
        }

        if (!AutoArchiveStore.TryGetPlayerRecord(playerId, out PlayerActivityRecord record))
        {
            return false;
        }

        if (ZoneSaviorSteamIds.TryNormalizePlatformId(record.PlatformId, out string steamId) &&
            ProtectedSteamIds.Contains(steamId))
        {
            reason = $"player {playerId} is protected by zones.yml steam_ids";
            return true;
        }

        foreach (string name in record.Names)
        {
            if (!string.IsNullOrWhiteSpace(name) && ProtectedPlayerNames.Contains(name.Trim()))
            {
                reason = $"player {playerId} is protected by zones.yml player_names ({name.Trim()})";
                return true;
            }
        }

        return false;
    }

    private static void EnsureFileExists()
    {
        if (File.Exists(ZoneSaviorPlugin.ZoneRuleFileFullPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ZoneSaviorPlugin.ZoneRuleFileFullPath)!);
        File.WriteAllText(ZoneSaviorPlugin.ZoneRuleFileFullPath, BuildDefaultYaml());
    }

    private static void OnSyncedYamlChanged()
    {
        if (_syncedYaml == null)
        {
            return;
        }

        ApplyYaml(_syncedYaml.Value, ZoneSaviorPlugin.ConfigSync.IsSourceOfTruth ? "local file" : "server sync");
    }

    private static void OnSourceOfTruthChanged(bool isSourceOfTruth)
    {
        if (isSourceOfTruth)
        {
            ReloadFromDisk();
        }
        else if (_syncedYaml != null)
        {
            ApplyYaml(_syncedYaml.Value, "server sync");
        }
    }

    private static void ApplyYaml(string yaml, string source)
    {
        if (!TryParseYaml(yaml, out List<ZoneLimitRule> parsedRules, out ZoneArchiveProtection parsedProtection, out string error))
        {
            _logger?.LogError($"Failed to parse zone rule YAML from {source}: {error}");
            return;
        }

        ApplyConfiguration(parsedRules, parsedProtection);
        _logger?.LogInfo($"Loaded {Rules.Count} zone limit rule(s) from {source}.");
    }

    private static void ApplyConfiguration(List<ZoneLimitRule> parsedRules, ZoneArchiveProtection parsedProtection)
    {
        Rules.Clear();
        Rules.AddRange(parsedRules);

        ProtectedPlayerIds.Clear();
        foreach (long playerId in parsedProtection.PlayerIds)
        {
            if (playerId != 0)
            {
                ProtectedPlayerIds.Add(playerId);
            }
        }

        ProtectedSteamIds.Clear();
        foreach (string steamId in parsedProtection.SteamIds)
        {
            string normalized = ZoneSaviorSteamIds.IsBareSteamId64(steamId) ? steamId.Trim() : "";
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                ProtectedSteamIds.Add(normalized);
            }
        }

        ProtectedPlayerNames.Clear();
        foreach (string name in parsedProtection.PlayerNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                ProtectedPlayerNames.Add(name.Trim());
            }
        }

        ZonePieceCounter.RebuildCounts();
    }

    private static bool TryParseYaml(string yaml, out List<ZoneLimitRule> rules, out ZoneArchiveProtection protection, out string error)
    {
        rules = [];
        protection = default;
        error = "";

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return true;
        }

        ZoneLimitFile? file;
        try
        {
            file = Deserializer.Deserialize<ZoneLimitFile>(yaml);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (file == null)
        {
            return true;
        }

        if (!ZoneArchiveProtection.TryCreateFromFile(file.ArchiveProtection, out protection, out string protectionError))
        {
            error = $"archive_protection.{protectionError}";
            return false;
        }

        bool defaultCountCreatorless = file.Defaults?.CountCreatorless ?? false;
        file.Rules ??= [];

        for (int i = 0; i < file.Rules.Count; i++)
        {
            ZoneLimitRuleFile? rawRule = file.Rules[i];
            if (rawRule == null)
            {
                error = $"rules[{i}] is null.";
                return false;
            }

            if (!TryNormalizeRange(rawRule.X, out int minX, out int maxX, out string xError))
            {
                error = $"rules[{i}].x {xError}";
                return false;
            }

            if (!TryNormalizeRange(rawRule.Z, out int minZ, out int maxZ, out string zError))
            {
                error = $"rules[{i}].z {zError}";
                return false;
            }

            if (rawRule.Limit < 0)
            {
                error = $"rules[{i}].limit must be zero or greater.";
                return false;
            }

            bool countCreatorless = rawRule.CountCreatorless ?? defaultCountCreatorless;
            rules.Add(new ZoneLimitRule(minX, maxX, minZ, maxZ, rawRule.Limit, countCreatorless, rawRule.Name ?? $"rule_{i + 1}"));
        }

        return true;
    }

    private static bool TryNormalizeRange(IReadOnlyList<int>? range, out int min, out int max, out string error)
    {
        min = 0;
        max = 0;
        error = "";

        if (range == null || range.Count != 2)
        {
            error = "must contain exactly two integers like [-1, 3].";
            return false;
        }

        min = Math.Min(range[0], range[1]);
        max = Math.Max(range[0], range[1]);
        return true;
    }

    private static string BuildDefaultYaml()
    {
        return
            "# ZoneSavior zone rules\n" +
            "# Rules are evaluated from top to bottom.\n" +
            "# The first matching rule wins.\n" +
            "# If a zone does not match any rule, it is unlimited.\n" +
            "version: 1\n" +
            "archive_protection:\n" +
            "  steam_ids:\n" +
            "    - \"76561198000000000\"\n" +
            "    - \"76561198000000001\"\n" +
            "  player_ids:\n" +
            "    - 123456789\n" +
            "  player_names:\n" +
            "    - \"GreatViking\"\n" +
            "    - \"Xcxcx\"\n" +
            "defaults:\n" +
            "  count_creatorless: false\n" +
            "rules:\n" +
            "  - name: center_keep\n" +
            "    x: [-1, 1]\n" +
            "    z: [-1, 1]\n" +
            "    limit: 40\n" +
            "    count_creatorless: true\n" +
            "  - name: outer_ring\n" +
            "    x: [-3, 3]\n" +
            "    z: [-3, 3]\n" +
            "    limit: 80\n";
    }
}

internal sealed class ZoneLimitFile
{
    public int Version { get; set; } = 1;

    public ZoneLimitDefaultsFile? Defaults { get; set; }

    public ZoneArchiveProtectionFile? ArchiveProtection { get; set; }

    public List<ZoneLimitRuleFile> Rules { get; set; } = [];
}

internal sealed class ZoneArchiveProtectionFile
{
    public List<string> SteamIds { get; set; } = [];

    public List<long> PlayerIds { get; set; } = [];

    public List<string> PlayerNames { get; set; } = [];
}

internal sealed class ZoneLimitDefaultsFile
{
    public bool CountCreatorless { get; set; }
}

internal sealed class ZoneLimitRuleFile
{
    public string? Name { get; set; }

    public List<int>? X { get; set; }

    public List<int>? Z { get; set; }

    public int Limit { get; set; }

    public bool? CountCreatorless { get; set; }
}

internal readonly struct ZoneLimitRule
{
    public ZoneLimitRule(int minX, int maxX, int minZ, int maxZ, int limit, bool countCreatorless, string name)
    {
        MinX = minX;
        MaxX = maxX;
        MinZ = minZ;
        MaxZ = maxZ;
        Limit = limit;
        CountCreatorless = countCreatorless;
        Name = name;
    }

    public int MinX { get; }
    public int MaxX { get; }
    public int MinZ { get; }
    public int MaxZ { get; }
    public int Limit { get; }
    public bool CountCreatorless { get; }
    public string Name { get; }

    public bool Contains(Vector2i zone)
    {
        return zone.x >= MinX && zone.x <= MaxX && zone.y >= MinZ && zone.y <= MaxZ;
    }
}

internal readonly struct ZoneArchiveProtection
{
    private readonly IReadOnlyList<string>? _steamIds;
    private readonly IReadOnlyList<long>? _playerIds;
    private readonly IReadOnlyList<string>? _playerNames;

    private ZoneArchiveProtection(List<string> steamIds, List<long> playerIds, List<string> playerNames)
    {
        _steamIds = steamIds;
        _playerIds = playerIds;
        _playerNames = playerNames;
    }

    public IReadOnlyList<string> SteamIds => _steamIds ?? [];
    public IReadOnlyList<long> PlayerIds => _playerIds ?? [];
    public IReadOnlyList<string> PlayerNames => _playerNames ?? [];

    public static bool TryCreateFromFile(ZoneArchiveProtectionFile? file, out ZoneArchiveProtection protection, out string error)
    {
        protection = default;
        error = "";

        for (int i = 0; i < (file?.SteamIds?.Count ?? 0); i++)
        {
            string steamId = file!.SteamIds[i];
            if (!ZoneSaviorSteamIds.IsBareSteamId64(steamId))
            {
                error = $"steam_ids[{i}] must be a quoted 17-digit SteamID64 like \"76561198000000000\". The steam: prefix is not supported here.";
                return false;
            }
        }

        protection = new ZoneArchiveProtection(
            file?.SteamIds ?? [],
            file?.PlayerIds ?? [],
            file?.PlayerNames ?? []);
        return true;
    }
}


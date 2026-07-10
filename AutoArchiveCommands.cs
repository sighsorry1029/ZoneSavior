using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using UnityEngine;

namespace ZoneSavior;

internal static class AutoArchiveCommands
{
    private const string ScanCommand = "zs_scan";
    private const string StatusCommand = "zs_status";
    private const string DebugZoneCommand = "zs_debugzone";
    private const string RequestRpcName = ZoneSaviorPlugin.ModGUID + "_AutoArchiveCommandRequest";
    private const string ResultRpcName = ZoneSaviorPlugin.ModGUID + "_AutoArchiveCommandResult";
    private static readonly Regex ZoneSpecPattern = new(@"^\s*\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)\s*$", RegexOptions.Compiled);

    private static ManualLogSource _logger = null!;
    private static bool _initialized;
    private static readonly ZoneRpcRegistrar RpcRegistrar = new();

    public static void Initialize(ManualLogSource logger)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _logger = logger;

        _ = new Terminal.ConsoleCommand(ScanCommand, "[steamID] [dry|save|reset] - Runs an auto archive scan, optionally filtered to one Steam owner.", HandleCommand);
        _ = new Terminal.ConsoleCommand(StatusCommand, "- Writes a YAML report with recent auto archive runs.", HandleCommand);
        _ = new Terminal.ConsoleCommand(DebugZoneCommand, "(x,z) - Writes a YAML report explaining auto archive eligibility for one zone.", HandleCommand);

        RegisterRpcs();
    }

    internal static void RegisterRpcs()
    {
        RpcRegistrar.EnsureRegistered(routedRpc =>
        {
            routedRpc.Register<ZPackage>(RequestRpcName, RPC_HandleRequest);
            routedRpc.Register<ZPackage>(ResultRpcName, RPC_HandleResult);
        });
    }

    private static void HandleCommand(Terminal.ConsoleEventArgs args)
    {
        EnsureCommandReady();
        AutoArchiveCommandRequest request = new()
        {
            Command = args.Args.Length > 0 ? args.Args[0] : "",
            Args = args.Args.ToList()
        };

        DispatchRequest(request, args.Context);
    }

    private static void DispatchRequest(AutoArchiveCommandRequest request, Terminal? context)
    {
        if (ZNet.instance.IsServer())
        {
            StartRequest(request, result => ShowResult(result, context));
            return;
        }

        RegisterRpcs();
        ZPackage package = new();
        package.Write(ZoneBundleSerialization.Serialize(request));
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpcName, package);
        context?.AddString($"{request.Command} request sent to server.");
    }

    private static void RPC_HandleRequest(long sender, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            if (!ZoneBundleCommands.IsAuthorizedSender(sender))
            {
                SendResult(sender, AutoArchiveCommandResult.Fail("Admin only."));
                return;
            }

            AutoArchiveCommandRequest request = ZoneBundleSerialization.Deserialize<AutoArchiveCommandRequest>(package.ReadString());
            StartRequest(request, result => SendResult(sender, result));
        }
        catch (Exception ex)
        {
            _logger.LogError($"Auto archive command RPC failed: {ex}");
            SendResult(sender, AutoArchiveCommandResult.Fail(ex.Message));
        }
    }

    private static void SendResult(long target, AutoArchiveCommandResult result)
    {
        if (ZRoutedRpc.instance == null)
        {
            return;
        }

        ZPackage response = new();
        response.Write(ZoneBundleSerialization.Serialize(result));
        ZRoutedRpc.instance.InvokeRoutedRPC(target, ResultRpcName, response);
    }

    private static void RPC_HandleResult(long sender, ZPackage package)
    {
        if (!ZoneRpcRegistrar.IsServerSender(sender))
        {
            return;
        }

        try
        {
            AutoArchiveCommandResult result = ZoneBundleSerialization.Deserialize<AutoArchiveCommandResult>(package.ReadString());
            ShowResult(result, Console.instance);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to read auto archive command result: {ex.Message}");
        }
    }

    private static void StartRequest(AutoArchiveCommandRequest request, Action<AutoArchiveCommandResult> onComplete)
    {
        onComplete(ExecuteRequest(request));
    }

    private static AutoArchiveCommandResult ExecuteRequest(AutoArchiveCommandRequest request)
    {
        List<string> messages = [];
        try
        {
            string command = request.Command;
            string[] args = request.Args?.ToArray() ?? [];
            if (string.IsNullOrWhiteSpace(command) && args.Length > 0)
            {
                command = args[0];
            }

            switch (command)
            {
                case ScanCommand:
                    ExecuteScan(args, messages);
                    break;
                case StatusCommand:
                    ExecuteStatus(messages);
                    break;
                case DebugZoneCommand:
                    ExecuteDebugZone(args, messages);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported auto archive command '{command}'.");
            }

            return AutoArchiveCommandResult.Ok(messages);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Auto archive command '{request.Command}' failed: {ex}");
            return AutoArchiveCommandResult.Fail(ex.Message);
        }
    }

    private static void ExecuteScan(string[] args, List<string> messages)
    {
        IEnumerable<string> modeArgs = args.Skip(1);
        List<long>? targetPlayerIds = null;
        string targetLabel = "";
        if (args.Length > 1 && !IsArchiveModeToken(args[1]))
        {
            targetPlayerIds = ResolveTargetPlayerIds(args[1], out targetLabel);
            modeArgs = args.Skip(2);
        }

        ParseMode(modeArgs, out bool? dryRun, out bool? reset);
        bool queued = targetPlayerIds == null
            ? AutoArchiveService.QueueManualScan(dryRun, reset)
            : AutoArchiveService.QueueManualScan(dryRun, reset, targetPlayerIds);
        if (!queued)
        {
            messages.Add("Auto archive scan could not be started. World may not be ready or another scan is running.");
            return;
        }

        messages.Add(targetPlayerIds == null
            ? "Auto archive scan started."
            : $"Auto archive scan started for {targetLabel}.");
    }

    private static void ExecuteStatus(List<string> messages)
    {
        AutoArchiveStatusReport report = BuildStatusReport();
        string path = WriteStatusReport(report);

        messages.Add($"Archive status report: {report.Runs.Count}/{report.TotalRuns} run(s).");
        if (report.Runs.Count > 0)
        {
            ArchiveRunRecord latest = report.Runs[0];
            messages.Add(
                $"Latest: {latest.RunId}, dry={latest.DryRun}, reset={latest.ResetAfterSave}, candidates={latest.CandidateZones}, processed={latest.ProcessedZones}, clusters={latest.Clusters.Count}.");
        }

        messages.Add($"Wrote YAML: {path}");
    }

    private static AutoArchiveStatusReport BuildStatusReport()
    {
        List<ArchiveRunRecord> runs = AutoArchiveStore.State.Runs
            .OrderByDescending(run => run.CreatedUtc)
            .ToList();

        return new AutoArchiveStatusReport
        {
            World = ZNet.instance?.GetWorldName() ?? "unknown",
            CreatedAt = ZoneSaviorTimestamp.Format(DateTime.UtcNow),
            TotalRuns = AutoArchiveStore.State.Runs.Count,
            Runs = runs
        };
    }

    private static string WriteStatusReport(AutoArchiveStatusReport report)
    {
        string directory = Path.Combine(ZoneSaviorPlugin.DataStorageFullPath, "Diagnostics");
        Directory.CreateDirectory(directory);
        string fileName = $"archive_status_{DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.yml";
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, ZoneBundleSerialization.Serialize(report));
        return path;
    }

    private static void ExecuteDebugZone(string[] args, List<string> messages)
    {
        Vector2i zone = ParseZoneSpec(args.Skip(1));
        ZoneSaviorBuildRecipeRules.RefreshIndex();
        AutoArchiveZoneDebugReport report = BuildZoneDebugReport(zone);
        string path = WriteZoneDebugReport(report);

        messages.Add(
            $"Archive debug zone ({zone.x},{zone.y}): zdo={report.Summary.TotalZdos}, candidatePieces={report.Summary.AutoArchiveCandidatePieces}, creators={report.Summary.CandidateCreators}, wouldCandidate={report.Summary.WouldBeCandidateZone}.");
        messages.Add($"Reason: {report.Summary.Reason}");
        messages.Add($"Wrote YAML: {path}");
    }

    private static AutoArchiveZoneDebugReport BuildZoneDebugReport(Vector2i zone)
    {
        if (ZDOMan.instance == null || ZNetScene.instance == null || ZoneSystem.instance == null)
        {
            throw new InvalidOperationException("World ZDO systems are not ready.");
        }

        List<ZDO> objects = [];
        ZDOMan.instance.FindObjects(zone, objects);
        DateTime utcNow = DateTime.UtcNow;
        AutoArchiveZoneDebugReport report = new()
        {
            World = ZNet.instance?.GetWorldName() ?? "unknown",
            CreatedAt = ZoneSaviorTimestamp.Format(utcNow),
            Zone = ZoneSaviorZones.ToModel(zone),
            Settings = new AutoArchiveZoneDebugSettings
            {
                DryRun = AutoArchiveConfig.DryRun,
                ResetAfterSave = AutoArchiveConfig.ResetAfterSave,
                InactiveDays = AutoArchiveConfig.InactiveDays,
                MinimumPiecesPerCluster = AutoArchiveConfig.MinimumPiecesPerCluster,
                MaxZonesPerRun = AutoArchiveConfig.MaxZonesPerRun
            }
        };

        HashSet<ZDOID> seen = [];
        foreach (ZDO zdo in objects)
        {
            if (zdo == null || !zdo.IsValid() || !seen.Add(zdo.m_uid))
            {
                continue;
            }

            AutoArchiveZoneDebugObject entry = BuildZoneDebugObject(zone, zdo);
            report.Objects.Add(entry);
            AddExclusionCounts(report.ExclusionCounts, entry.ExclusionReasons);
        }

        List<long> creatorIds = report.Objects
            .Where(entry => entry.AutoArchiveCandidatePiece)
            .Select(entry => entry.CreatorPlayerId)
            .Where(playerId => playerId != 0L)
            .Distinct()
            .OrderBy(playerId => playerId)
            .ToList();

        report.Creators = creatorIds
            .Select(playerId => BuildCreatorDebug(playerId, utcNow))
            .ToList();

        bool allCreatorsEligible = report.Creators.Count > 0 && report.Creators.All(creator => creator.Eligible);
        report.Summary = new AutoArchiveZoneDebugSummary
        {
            TotalZdos = report.Objects.Count,
            InRequestedZone = report.Objects.Count(entry => entry.InRequestedZone),
            WearNTear = report.Objects.Count(entry => entry.HasWearNTear),
            PlayerBuildRecipe = report.Objects.Count(entry => entry.HasBuildRecipe),
            AutoArchiveCandidatePieces = report.Objects.Count(entry => entry.AutoArchiveCandidatePiece),
            CandidateCreators = creatorIds.Count,
            ObjectDbReady = ObjectDB.instance != null,
            WouldBeCandidateZone = report.Objects.Any(entry => entry.AutoArchiveCandidatePiece) && allCreatorsEligible
        };
        report.Summary.Reason = BuildDebugSummaryReason(report, allCreatorsEligible);

        return report;
    }

    private static AutoArchiveZoneDebugObject BuildZoneDebugObject(Vector2i requestedZone, ZDO zdo)
    {
        ZoneStructureInfo info = ZoneStructureClassifier.Inspect(zdo, requestedZone);

        return new AutoArchiveZoneDebugObject
        {
            ZdoId = info.ZdoId,
            PrefabHash = info.PrefabHash,
            Prefab = info.Prefab,
            Position = [Round(info.Position.x), Round(info.Position.y), Round(info.Position.z)],
            ObjectZone = ZoneSaviorZones.ToModel(info.ObjectZone),
            CreatorPlayerId = info.CreatorPlayerId,
            CreatorName = info.CreatorName,
            HasPrefab = info.HasPrefab,
            HasZNetView = info.HasZNetView,
            HasWearNTear = info.HasWearNTear,
            HasPiece = info.HasPiece,
            HasBuildRecipe = info.HasBuildRecipe,
            InRequestedZone = info.InRequestedZone,
            AutoArchiveCandidatePiece = info.AutoArchiveCandidatePiece,
            ExclusionReasons = info.ExclusionReasons
        };
    }

    private static AutoArchiveZoneDebugCreator BuildCreatorDebug(long playerId, DateTime utcNow)
    {
        AutoArchiveCreatorEligibility evaluation = AutoArchiveStore.EvaluateCreatorArchiveEligibility(
            playerId,
            utcNow,
            AutoArchiveConfig.InactiveDays,
            recordUnknownPlayer: false);

        return new AutoArchiveZoneDebugCreator
        {
            PlayerId = evaluation.PlayerId,
            PlatformId = evaluation.PlatformId,
            Names = evaluation.Names,
            RecordedInActivity = evaluation.RecordedInActivity,
            UnknownActivityRecord = evaluation.UnknownActivityRecord,
            Protected = evaluation.Protected,
            Eligible = evaluation.Eligible,
            Reason = evaluation.Reason
        };
    }

    private static string BuildDebugSummaryReason(AutoArchiveZoneDebugReport report, bool allCreatorsEligible)
    {
        if (report.Summary.AutoArchiveCandidatePieces == 0)
        {
            return "No WearNTear with a non-zero creator and a registered player build recipe was found in this zone.";
        }

        if (!allCreatorsEligible)
        {
            string reasons = string.Join("; ", report.Creators.Where(creator => !creator.Eligible).Select(creator => creator.Reason));
            return $"At least one candidate creator is not archive-eligible: {reasons}";
        }

        if (report.Summary.AutoArchiveCandidatePieces < AutoArchiveConfig.MinimumPiecesPerCluster)
        {
            return $"Zone has candidate pieces, but this single-zone piece count is below Minimum Pieces Per Cluster ({report.Summary.AutoArchiveCandidatePieces}/{AutoArchiveConfig.MinimumPiecesPerCluster}). Cluster adjacency may still change the final action.";
        }

        return "This zone would be an auto archive candidate before cluster adjacency and max-zones-per-run checks.";
    }

    private static string WriteZoneDebugReport(AutoArchiveZoneDebugReport report)
    {
        string directory = Path.Combine(ZoneSaviorPlugin.DataStorageFullPath, "Diagnostics");
        Directory.CreateDirectory(directory);
        string fileName = $"archive_debug_zone_{report.Zone.X}_{report.Zone.Z}_{DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.yml";
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, ZoneBundleSerialization.Serialize(report));
        return path;
    }

    private static Vector2i ParseZoneSpec(IEnumerable<string> values)
    {
        string value = string.Join(" ", values).Trim();
        Match match = ZoneSpecPattern.Match(value);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
        {
            throw new InvalidOperationException($"Syntax: {DebugZoneCommand} (x,z)");
        }

        return new Vector2i(x, z);
    }

    private static void AddExclusionCounts(Dictionary<string, int> counts, IEnumerable<string> reasons)
    {
        foreach (string reason in reasons)
        {
            counts.TryGetValue(reason, out int count);
            counts[reason] = count + 1;
        }
    }

    private static float Round(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }

    private static void ParseMode(IEnumerable<string> args, out bool? dryRun, out bool? reset)
    {
        dryRun = null;
        reset = null;
        foreach (string arg in args)
        {
            if (IsArchiveModeToken(arg, "dry") ||
                IsArchiveModeToken(arg, "dry-run"))
            {
                dryRun = true;
                reset = false;
            }
            else if (IsArchiveModeToken(arg, "save"))
            {
                dryRun = false;
                reset = false;
            }
            else if (IsArchiveModeToken(arg, "reset"))
            {
                dryRun = false;
                reset = true;
            }
            else
            {
                throw new InvalidOperationException($"Unknown archive mode '{arg}'. Use dry, save, or reset.");
            }
        }
    }

    private static bool IsArchiveModeToken(string arg)
    {
        return IsArchiveModeToken(arg, "dry") ||
               IsArchiveModeToken(arg, "dry-run") ||
               IsArchiveModeToken(arg, "save") ||
               IsArchiveModeToken(arg, "reset");
    }

    private static bool IsArchiveModeToken(string arg, string expected)
    {
        return string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static List<long> ResolveTargetPlayerIds(string target, out string targetLabel)
    {
        if (AutoArchiveStore.TryGetPlayerIdsBySteamId(target, out List<long> playerIds, out string normalizedSteamId))
        {
            targetLabel = $"steamID {normalizedSteamId} (playerID {string.Join(", ", playerIds)})";
            return playerIds;
        }

        if (ZoneSaviorSteamIds.LooksLikeSteamId(target))
        {
            throw new InvalidOperationException(
                $"No known playerID is linked to SteamID {target}. The player must have joined while ZoneSavior activity tracking was active.");
        }

        throw new InvalidOperationException($"Syntax: {ScanCommand} [steamID] [dry|save|reset]");
    }

    private static void EnsureCommandReady()
    {
        if (ZNet.instance == null)
        {
            throw new InvalidOperationException("World is not ready.");
        }

        if (!ZNet.instance.IsServer() && ZRoutedRpc.instance == null)
        {
            throw new InvalidOperationException("Server RPC is not ready.");
        }

        if (!ZNet.instance.IsServer())
        {
            return;
        }

        if (ZNet.instance.IsServer() && Player.m_localPlayer == null)
        {
            return;
        }

        if (!ZNet.instance.LocalPlayerIsAdminOrHost())
        {
            throw new InvalidOperationException("Admin only.");
        }
    }

    private static void ShowResult(AutoArchiveCommandResult result, Terminal? terminal)
    {
        MessageHud.MessageType messageType = result.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center;
        List<string> messages = result.Messages.Count > 0 ? result.Messages : [result.Success ? "Done." : "Command failed."];
        foreach (string message in messages)
        {
            _logger.LogInfo(message);
            terminal?.AddString(message);
            if (Player.m_localPlayer != null)
            {
                Player.m_localPlayer.Message(messageType, message);
            }
        }
    }

}

internal sealed class AutoArchiveCommandRequest
{
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
}

internal sealed class AutoArchiveCommandResult
{
    public bool Success { get; set; }
    public List<string> Messages { get; set; } = [];

    public static AutoArchiveCommandResult Ok(IEnumerable<string> messages)
    {
        return new AutoArchiveCommandResult
        {
            Success = true,
            Messages = messages.ToList()
        };
    }

    public static AutoArchiveCommandResult Fail(string message)
    {
        return new AutoArchiveCommandResult
        {
            Success = false,
            Messages = [message]
        };
    }
}


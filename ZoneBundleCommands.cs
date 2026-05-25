using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using DataEntry = ZoneSavior.ZoneBundleZdoData;
using DataHelper = ZoneSavior.ZoneBundleZdoHelper;

namespace ZoneSavior;

internal static partial class ZoneBundleCommands
{
    internal const string SaveOperation = "zs_savezone";
    internal const string LoadOperation = "zs_loadzone";
    private const string ClientTerrainApplyRequestRpcName = ZoneSaviorPlugin.ModGUID + "_ZoneBundleClientTerrainApplyRequest";
    private const string ClientTerrainApplyResponseRpcName = ZoneSaviorPlugin.ModGUID + "_ZoneBundleClientTerrainApplyResponse";
    private const string ClientTerrainCaptureRequestRpcName = ZoneSaviorPlugin.ModGUID + "_ZoneBundleClientTerrainCaptureRequest";
    private const string ClientTerrainCaptureResponseRpcName = ZoneSaviorPlugin.ModGUID + "_ZoneBundleClientTerrainCaptureResponse";
    private const string TerrainWitnessAnnounceRpcName = ZoneSaviorPlugin.ModGUID + "_ZoneBundleTerrainWitnessAnnounce";
    private const string WearNTearSanitize = "wearntear-v1";
    private const string MonsterSanitize = "monster-v1";
    private const string TamedMonsterSanitize = "tamed-monster-v1";
    private const int CaptureBatchSize = 1000;
    private const int ResetBatchSize = 1000;
    private const int TerrainRecalcBatchSize = 8;
    private const float ClientTerrainApplyTimeoutSeconds = 180f;
    private const float TerrainWitnessSearchRadius = 160f;

    private static readonly Dictionary<string, string> EmptyParameters = new();

    private static ManualLogSource _logger = null!;
    private static bool _initialized;
    private static readonly ZoneRpcRegistrar RpcRegistrar = new();
    private static readonly Dictionary<string, ZoneBundleClientTerrainApplyResponse> ClientTerrainApplyResponses = new();
    private static readonly Dictionary<string, ZoneBundleClientTerrainCaptureResponse> ClientTerrainCaptureResponses = new();
    private static readonly HashSet<long> TerrainWitnessPeers = [];
    private static long _announcedTerrainWitnessServer;

    public static void Initialize(ManualLogSource logger)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _logger = logger;

        ZoneBundleCommandEndpoint.Initialize(logger);
        RegisterRpcs();
    }

    internal static void RegisterRpcs()
    {
        ZoneBundleCommandEndpoint.RegisterRpcs();
        RpcRegistrar.EnsureRegistered(routedRpc =>
        {
            routedRpc.Register<ZPackage>(ClientTerrainApplyRequestRpcName, RPC_HandleClientTerrainApplyRequest);
            routedRpc.Register<ZPackage>(ClientTerrainApplyResponseRpcName, RPC_HandleClientTerrainApplyResponse);
            routedRpc.Register<ZPackage>(ClientTerrainCaptureRequestRpcName, RPC_HandleClientTerrainCaptureRequest);
            routedRpc.Register<ZPackage>(ClientTerrainCaptureResponseRpcName, RPC_HandleClientTerrainCaptureResponse);
            routedRpc.Register<ZPackage>(TerrainWitnessAnnounceRpcName, RPC_HandleTerrainWitnessAnnounce);
        });

        AnnounceTerrainWitnessIfReady();
    }

    private static void RPC_HandleClientTerrainApplyRequest(long sender, ZPackage package)
    {
        if (ZNet.instance && ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            ZoneBundleClientTerrainApplyRequest request = ZoneBundleSerialization.Deserialize<ZoneBundleClientTerrainApplyRequest>(package.ReadString());
            if (ZoneSaviorPlugin.Instance == null)
            {
                SendClientTerrainApplyResponse(sender, new ZoneBundleClientTerrainApplyResponse
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Message = "ZoneSavior client plugin instance is not available."
                });
                return;
            }

            ZoneSaviorPlugin.Instance.StartCoroutine(ApplyClientTerrainRequestAsync(sender, request));
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle client terrain RPC failed: {ex}");
        }
    }

    private static void RPC_HandleClientTerrainApplyResponse(long sender, ZPackage package)
    {
        if (!ZNet.instance || !ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            ZoneBundleClientTerrainApplyResponse response = ZoneBundleSerialization.Deserialize<ZoneBundleClientTerrainApplyResponse>(package.ReadString());
            if (!string.IsNullOrWhiteSpace(response.RequestId))
            {
                ClientTerrainApplyResponses[response.RequestId] = response;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle client terrain response failed: {ex}");
        }
    }

    private static void RPC_HandleClientTerrainCaptureRequest(long sender, ZPackage package)
    {
        if (ZNet.instance && ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            ZoneBundleClientTerrainCaptureRequest request = ZoneBundleSerialization.Deserialize<ZoneBundleClientTerrainCaptureRequest>(package.ReadString());
            ZoneBundleClientTerrainCaptureResponse response = CaptureClientTerrainContacts(request);
            SendClientTerrainCaptureResponse(sender, response);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle client terrain capture RPC failed: {ex}");
        }
    }

    private static void RPC_HandleClientTerrainCaptureResponse(long sender, ZPackage package)
    {
        if (!ZNet.instance || !ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            ZoneBundleClientTerrainCaptureResponse response = ZoneBundleSerialization.Deserialize<ZoneBundleClientTerrainCaptureResponse>(package.ReadString());
            if (!string.IsNullOrWhiteSpace(response.RequestId))
            {
                ClientTerrainCaptureResponses[response.RequestId] = response;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle client terrain capture response failed: {ex}");
        }
    }

    private static void RPC_HandleTerrainWitnessAnnounce(long sender, ZPackage package)
    {
        if (!ZNet.instance || !ZNet.instance.IsServer())
        {
            return;
        }

        TerrainWitnessPeers.Add(sender);
    }

    private static void SendClientTerrainApplyResponse(long target, ZoneBundleClientTerrainApplyResponse result)
    {
        ZPackage response = new();
        response.Write(ZoneBundleSerialization.Serialize(result));
        ZRoutedRpc.instance.InvokeRoutedRPC(target, ClientTerrainApplyResponseRpcName, response);
    }

    private static void SendClientTerrainCaptureResponse(long target, ZoneBundleClientTerrainCaptureResponse result)
    {
        ZPackage response = new();
        response.Write(ZoneBundleSerialization.Serialize(result));
        ZRoutedRpc.instance.InvokeRoutedRPC(target, ClientTerrainCaptureResponseRpcName, response);
    }

    private static void AnnounceTerrainWitnessIfReady()
    {
        if (ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null)
        {
            return;
        }

        long serverPeer = ZRoutedRpc.instance.GetServerPeerID();
        if (serverPeer == 0L || _announcedTerrainWitnessServer == serverPeer)
        {
            return;
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(serverPeer, TerrainWitnessAnnounceRpcName, new ZPackage());
        _announcedTerrainWitnessServer = serverPeer;
    }

    internal static void StartRequest(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete, long terrainAssistPeer = 0L)
    {
        if (request.Operation == SaveOperation && ZoneSaviorPlugin.Instance != null)
        {
            ZoneSaviorPlugin.Instance.StartCoroutine(ExecuteSaveRequestAsync(request, onComplete, terrainAssistPeer));
            return;
        }

        if (request.Operation == LoadOperation && ZoneSaviorPlugin.Instance != null)
        {
            ZoneSaviorPlugin.Instance.StartCoroutine(ExecuteLoadRequestAsync(request, onComplete, terrainAssistPeer));
            return;
        }

        onComplete(ExecuteRequest(request));
    }

    private static IEnumerator ExecuteSaveRequestAsync(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete, long terrainAssistPeer)
    {
        ZoneBundleArchiveResult? archiveResult = null;
        yield return SaveZonesAsync(EnumerateZones(request.SourceRange), request.Tag, result => archiveResult = result, terrainAssistPeer);

        if (archiveResult == null)
        {
            onComplete(ZoneBundleCommandResult.Fail("Save failed: archive coroutine did not return a result."));
            yield break;
        }

        onComplete(archiveResult.Success
            ? ZoneBundleCommandResult.Ok(archiveResult.Message)
            : ZoneBundleCommandResult.Fail(archiveResult.Message));
    }

    private static IEnumerator ExecuteLoadRequestAsync(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete, long terrainAssistPeer)
    {
        ZoneBundleCommandResult result = ZoneBundleCommandResult.Fail("Load failed before it started.");
        if (request.Operation == LoadOperation)
        {
            if (request.RestoreOriginal)
            {
                yield return RestoreTagToOriginalZonesAsync(request.Tag, value => result = value);
            }
            else if (request.LoadSourceZone)
            {
                yield return LoadZoneRequestAsync(request, value => result = value, terrainAssistPeer);
            }
            else
            {
                yield return LoadArchiveManifestAsync(request, value => result = value, terrainAssistPeer);
            }
        }
        else
        {
            result = ExecuteRequest(request);
        }

        onComplete(result);
    }

    private static ZoneBundleCommandResult ExecuteRequest(ZoneBundleCommandRequest request)
    {
        try
        {
            return request.Operation switch
            {
                SaveOperation => SaveRange(request),
                LoadOperation => ExecuteLoadRequest(request),
                _ => ZoneBundleCommandResult.Fail($"Unsupported operation '{request.Operation}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle command '{request.Operation}' failed: {ex}");
            return ZoneBundleCommandResult.Fail(ex.Message);
        }
    }

    private static ZoneBundleCommandResult SaveRange(ZoneBundleCommandRequest request)
    {
        ZoneBundleArchiveResult result = SaveZones(EnumerateZones(request.SourceRange), request.Tag);
        return result.Success ? ZoneBundleCommandResult.Ok(result.Message) : ZoneBundleCommandResult.Fail(result.Message);
    }

    private static ZoneBundleCommandResult ExecuteLoadRequest(ZoneBundleCommandRequest request)
    {
        if (request.RestoreOriginal)
        {
            return RestoreTagToOriginalZones(request.Tag);
        }

        return request.LoadSourceZone ? LoadZoneRequest(request) : LoadArchiveManifest(request);
    }

    private static ZoneBundleCommandResult LoadZoneRequest(ZoneBundleCommandRequest request)
    {
        return LoadSingleZone(request);
    }

    private static IEnumerator LoadZoneRequestAsync(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete, long terrainAssistPeer)
    {
        yield return LoadSingleZoneAsync(request, onComplete, terrainAssistPeer);
    }

    private static bool IsSingleRange(ZoneBundleRange range)
    {
        return range.MinX == range.MaxX && range.MinZ == range.MaxZ;
    }

    internal static ZoneBundleArchiveResult SaveZones(IEnumerable<Vector2i> sourceZones, string tag)
    {
        List<Vector2i> zones = NormalizeZones(sourceZones);

        if (zones.Count == 0)
        {
            return new ZoneBundleArchiveResult
            {
                Success = false,
                Tag = tag,
                Message = "No zones to save."
            };
        }

        ZoneBundleManifest manifest = CreateManifest(tag, zones, out string manifestPath);

        ZoneBundleTerrain.TerrainSourceAnchor sourceAnchor = ZoneBundleTerrain.ComputeSupportAnchor(zones);
        ArchiveSaveProgress progress = new();
        foreach (Vector2i zone in zones)
        {
            ZoneBundleFile bundle = CaptureBundle(zone, tag, sourceAnchor, out int entryCount, out int monsterCount, out ZoneBundleTerrainCaptureState terrainState);
            StoreCapturedBundle(tag, manifest, zone, bundle, entryCount, monsterCount, terrainState, progress);
        }

        UpdateManifestSourceZoneCreators(manifest);
        ZoneBundleStore.SaveManifest(manifestPath, manifest);
        return CreateArchiveResult(true, tag, manifestPath, manifest, progress);
    }

    internal static IEnumerator SaveZonesAsync(IEnumerable<Vector2i> sourceZones, string tag, Action<ZoneBundleArchiveResult> onComplete, long terrainAssistPeer = 0L)
    {
        List<Vector2i> zones = NormalizeZones(sourceZones);

        if (zones.Count == 0)
        {
            onComplete(new ZoneBundleArchiveResult
            {
                Success = false,
                Tag = tag,
                Message = "No zones to save."
            });
            yield break;
        }

        ZoneBundleManifest manifest = CreateManifest(tag, zones, out string manifestPath);

        ZoneBundleTerrain.TerrainSourceAnchor sourceAnchor = new(float.NaN);
        yield return ZoneBundleTerrain.ComputeSupportAnchorAsync(zones, anchor => sourceAnchor = anchor);

        ArchiveSaveProgress progress = new();
        foreach (Vector2i zone in zones)
        {
            ZoneBundleFile bundle;
            int entryCount;
            int monsterCount;
            ZoneBundleTerrainCaptureState terrainState;
            CaptureBundleResult? capture = null;
            yield return CaptureBundleAsync(zone, tag, sourceAnchor, result => capture = result);
            if (capture == null || !capture.Success || capture.Bundle == null)
            {
                onComplete(CreateArchiveResult(
                    false,
                    tag,
                    manifestPath,
                    manifest,
                    progress,
                    $"save failed: {capture?.ErrorMessage ?? "capture coroutine did not return a result"}"));
                yield break;
            }

            bundle = capture.Bundle;
            entryCount = capture.EntryCount;
            monsterCount = capture.MonsterCount;
            terrainState = capture.TerrainState;

            if (terrainState == ZoneBundleTerrainCaptureState.NotLoaded)
            {
                ZoneBundleClientTerrainCaptureResponse? terrainResponse = null;
                string terrainCaptureFailure = "";
                foreach (long witnessPeer in GetTerrainWitnessCandidates(zone, terrainAssistPeer))
                {
                    yield return RequestClientTerrainCaptureAsync(witnessPeer, tag, zone, bundle, value => terrainResponse = value);
                    if (terrainResponse is { Success: true })
                    {
                        break;
                    }

                    terrainCaptureFailure = terrainResponse?.Message ?? "capture coroutine did not return a response";
                }

                if (terrainResponse is { Success: true, ContactsCaptured: true })
                {
                    bundle.TerrainContactsCaptured = true;
                    bundle.TerrainContacts = terrainResponse.Contacts;
                    terrainState = GetTerrainCaptureState(true, terrainResponse.Contacts.Count);
                    bundle.TerrainCaptureState = terrainState;
                }
                else if (!string.IsNullOrWhiteSpace(terrainCaptureFailure))
                {
                    _logger.LogWarning($"Client terrain capture skipped for zone ({zone.x},{zone.y}): {terrainCaptureFailure}");
                }
            }

            try
            {
                StoreCapturedBundle(tag, manifest, zone, bundle, entryCount, monsterCount, terrainState, progress);
            }
            catch (Exception ex)
            {
                onComplete(CreateArchiveResult(
                    false,
                    tag,
                    manifestPath,
                    manifest,
                    progress,
                    $"save failed: {ex.Message}"));
                yield break;
            }

            yield return null;
        }

        try
        {
            UpdateManifestSourceZoneCreators(manifest);
            ZoneBundleStore.SaveManifest(manifestPath, manifest);
        }
        catch (Exception ex)
        {
            onComplete(CreateArchiveResult(
                false,
                tag,
                manifestPath,
                manifest,
                progress,
                $"manifest save failed: {ex.Message}"));
            yield break;
        }

        onComplete(CreateArchiveResult(true, tag, manifestPath, manifest, progress));
    }

    private static void StoreCapturedBundle(
        string tag,
        ZoneBundleManifest manifest,
        Vector2i zone,
        ZoneBundleFile bundle,
        int entryCount,
        int monsterCount,
        ZoneBundleTerrainCaptureState terrainState,
        ArchiveSaveProgress progress)
    {
        progress.TotalEntries += entryCount;
        progress.TotalMonsters += monsterCount;
        if (terrainState != ZoneBundleTerrainCaptureState.NotLoaded)
        {
            progress.TerrainLoaded++;
        }

        if (terrainState == ZoneBundleTerrainCaptureState.Contacts)
        {
            progress.TerrainCaptured++;
        }

        string bundlePath = ZoneBundleStore.GetBundlePath(tag, ++progress.BundleIndex);
        ZoneBundleStore.SaveBundle(bundlePath, bundle);
        AddBundleToManifest(manifest, zone, bundlePath, bundle);
    }

    private static List<Vector2i> NormalizeZones(IEnumerable<Vector2i> zones)
    {
        return zones
            .Distinct()
            .OrderBy(zone => zone.y)
            .ThenBy(zone => zone.x)
            .ToList();
    }

    private static ZoneBundleManifest CreateManifest(string tag, List<Vector2i> zones, out string manifestPath)
    {
        ZoneBundleRange sourceRange = CreateRange(
            zones.Min(zone => zone.x),
            zones.Min(zone => zone.y),
            zones.Max(zone => zone.x),
            zones.Max(zone => zone.y));

        manifestPath = ZoneBundleStore.GetManifestPath(tag);
        return new ZoneBundleManifest
        {
            Tag = tag,
            World = GetWorldName(),
            SavedAt = ZoneSaviorTimestamp.Now(),
            SourceRange = sourceRange
        };
    }

    private static void AddBundleToManifest(ZoneBundleManifest manifest, Vector2i zone, string bundlePath, ZoneBundleFile bundle)
    {
        manifest.Bundles.Add(new ZoneBundleManifestEntry
        {
            Zone = ToModel(zone),
            File = Path.GetFileName(bundlePath),
            SourceZoneCreators = CloneSourceZoneCreators(bundle.SourceZoneCreators)
        });
    }

    private static ZoneBundleArchiveResult CreateArchiveResult(
        bool success,
        string tag,
        string manifestPath,
        ZoneBundleManifest manifest,
        ArchiveSaveProgress progress,
        string? message = null)
    {
        return CreateArchiveResult(
            success,
            tag,
            manifestPath,
            manifest,
            progress.TotalEntries,
            progress.TotalMonsters,
            progress.TerrainLoaded,
            progress.TerrainCaptured,
            message);
    }

    private static ZoneBundleArchiveResult CreateArchiveResult(
        bool success,
        string tag,
        string manifestPath,
        ZoneBundleManifest manifest,
        int totalEntries,
        int totalMonsters,
        int terrainLoaded,
        int terrainCaptured,
        string? message = null)
    {
        return new ZoneBundleArchiveResult
        {
            Success = success,
            Tag = tag,
            ManifestPath = manifestPath,
            ZoneCount = manifest.Bundles.Count,
            EntryCount = totalEntries,
            MonsterCount = totalMonsters,
            TerrainLoaded = terrainLoaded,
            TerrainCaptured = terrainCaptured,
            Message = message ?? BuildArchiveSuccessMessage(tag, manifestPath, manifest, totalEntries, totalMonsters, terrainLoaded, terrainCaptured)
        };
    }

    private static string BuildArchiveSuccessMessage(
        string tag,
        string manifestPath,
        ZoneBundleManifest manifest,
        int totalEntries,
        int totalMonsters,
        int terrainLoaded,
        int terrainCaptured)
    {
        return $"Saved {manifest.Bundles.Count} zone bundle(s) for tag '{tag}' to '{Path.GetDirectoryName(manifestPath)}' " +
               $"(entries: {totalEntries}, monsters: {totalMonsters}, terrain contacts: {terrainCaptured}/{manifest.Bundles.Count}, terrain loaded: {terrainLoaded}/{manifest.Bundles.Count}, mode: SupportFill).";
    }

    private sealed class ArchiveSaveProgress
    {
        public int BundleIndex;
        public int TotalEntries;
        public int TotalMonsters;
        public int TerrainLoaded;
        public int TerrainCaptured;
    }

    private static void UpdateManifestSourceZoneCreators(ZoneBundleManifest manifest)
    {
        Dictionary<long, ZoneBundleCreatorPlayer> players = [];
        foreach (ZoneBundleCreatorPlayer player in manifest.Bundles.SelectMany(entry => entry.SourceZoneCreators))
        {
            if (player.PlayerId == 0L || players.ContainsKey(player.PlayerId))
            {
                continue;
            }

            players[player.PlayerId] = CloneCreatorPlayer(player);
        }

        manifest.SourceZoneCreators = players
            .Values
            .OrderBy(player => player.PlayerId)
            .ToList();
    }

    private static void AddCreatorPlayer(Dictionary<long, string> creatorNames, long playerId, string observedName)
    {
        if (playerId == 0L)
        {
            return;
        }

        string normalizedName = NormalizeCreatorString(observedName) ?? "";
        if (!creatorNames.TryGetValue(playerId, out string existing) || string.IsNullOrWhiteSpace(existing))
        {
            creatorNames[playerId] = normalizedName;
        }
    }

    private static List<ZoneBundleCreatorPlayer> BuildSourceZoneCreators(IReadOnlyDictionary<long, string> creatorNames)
    {
        return creatorNames
            .Keys
            .Where(playerId => playerId != 0L)
            .OrderBy(playerId => playerId)
            .Select(playerId => BuildCreatorPlayer(
                playerId,
                creatorNames.TryGetValue(playerId, out string observedName) ? observedName : ""))
            .ToList();
    }

    private static ZoneBundleCreatorPlayer BuildCreatorPlayer(long playerId, string observedName)
    {
        return new ZoneBundleCreatorPlayer
        {
            PlayerId = playerId,
            Name = ResolveCreatorName(playerId, observedName),
            PlatformId = ResolveCreatorPlatformId(playerId)
        };
    }

    private static List<ZoneBundleCreatorPlayer> CloneSourceZoneCreators(IEnumerable<ZoneBundleCreatorPlayer> players)
    {
        return players
            .Where(player => player.PlayerId != 0L)
            .OrderBy(player => player.PlayerId)
            .Select(CloneCreatorPlayer)
            .ToList();
    }

    private static ZoneBundleCreatorPlayer CloneCreatorPlayer(ZoneBundleCreatorPlayer player)
    {
        return new ZoneBundleCreatorPlayer
        {
            PlayerId = player.PlayerId,
            Name = NormalizeCreatorString(player.Name ?? ""),
            PlatformId = NormalizeCreatorString(player.PlatformId ?? "")
        };
    }

    private static string? ResolveCreatorName(long playerId, string observedName)
    {
        string? normalizedObserved = NormalizeCreatorString(observedName);
        if (normalizedObserved != null)
        {
            return normalizedObserved;
        }

        if (!AutoArchiveStore.TryGetPlayerRecord(playerId, out PlayerActivityRecord record))
        {
            return null;
        }

        return NormalizeCreatorString(record.Names.LastOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            !string.Equals(candidate, "unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, "manual", StringComparison.OrdinalIgnoreCase)) ?? "");
    }

    private static string? ResolveCreatorPlatformId(long playerId)
    {
        if (!AutoArchiveStore.TryGetPlayerRecord(playerId, out PlayerActivityRecord record))
        {
            return null;
        }

        string? platformId = NormalizeCreatorString(record.PlatformId);
        if (platformId == null ||
            platformId.StartsWith("unknown:", StringComparison.OrdinalIgnoreCase) ||
            platformId.StartsWith("manual:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return platformId;
    }

    private static string? NormalizeCreatorString(string value)
    {
        string trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static ZoneBundleCommandResult LoadSingleZone(ZoneBundleCommandRequest request)
    {
        Vector2i sourceZone = ToSingleSourceZone(request.SourceRange);
        Vector2i targetZone = ToVector2i(request.TargetZone!);
        ZoneBundleFile bundle = ZoneBundleStore.LoadBundleFromManifestZone(request.Tag, sourceZone);

        List<LoadWorkItem> work = [new(targetZone, bundle)];

        ZoneLoadTotals totals = ApplyLoadWork(work, exactSource: false, request.YOffset);
        return ZoneBundleCommandResult.Ok(
            $"Loaded {request.Tag} source zone ({sourceZone.x},{sourceZone.y}) into target zone ({targetZone.x},{targetZone.y}) " +
            $"(removed: {totals.Removed}, created: {totals.Created}, terrain: {(totals.TerrainApplied > 0 ? "yes" : "no")}, mode: SupportFill, yOffset: {Round(request.YOffset)}).");
    }

    private static IEnumerator LoadSingleZoneAsync(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete, long terrainAssistPeer)
    {
        Vector2i sourceZone;
        Vector2i targetZone;
        ZoneBundleFile bundle;
        List<LoadWorkItem> work;
        try
        {
            sourceZone = ToSingleSourceZone(request.SourceRange);
            targetZone = ToVector2i(request.TargetZone!);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle async load failed: {ex}");
            onComplete(ZoneBundleCommandResult.Fail(ex.Message));
            yield break;
        }

        if (!ZoneBundleStore.TryLoadBundleFromManifestZone(request.Tag, sourceZone, out bundle, out string bundleReason))
        {
            _logger.LogError($"Zone bundle async load failed: {bundleReason}");
            onComplete(ZoneBundleCommandResult.Fail(bundleReason));
            yield break;
        }

        yield return null;

        work = [new(targetZone, bundle)];
        ZoneLoadTotals totals = default;
        TerrainPreparationResult terrainPreparation = default;
        string loadError = "";
        yield return PrepareAndApplyLoadWorkAsync("Zone bundle async load failed", request, work, false, request.YOffset, terrainAssistPeer, (loadTotals, preparation, error) =>
        {
            totals = loadTotals;
            terrainPreparation = preparation;
            loadError = error;
        });
        if (!string.IsNullOrWhiteSpace(loadError))
        {
            onComplete(ZoneBundleCommandResult.Fail(loadError));
            yield break;
        }

        onComplete(ZoneBundleCommandResult.Ok(
            $"Loaded {request.Tag} source zone ({sourceZone.x},{sourceZone.y}) into target zone ({targetZone.x},{targetZone.y}) " +
            $"(removed: {totals.Removed}, created: {totals.Created}, terrain: {(totals.TerrainApplied > 0 ? "yes" : "no")}, mode: SupportFill{terrainPreparation.Describe()}, yOffset: {Round(request.YOffset)})."));
    }

    private static ZoneBundleCommandResult LoadArchiveManifest(ZoneBundleCommandRequest request)
    {
        ZoneBundleManifest manifest = ZoneBundleStore.LoadManifest(request.Tag);
        if (manifest.Bundles.Count == 0)
        {
            return ZoneBundleCommandResult.Fail($"Manifest for tag '{request.Tag}' contains no zone bundles.");
        }

        Vector2i targetStart = ToVector2i(request.TargetZone!);
        List<LoadWorkItem> work = BuildArchiveLoadWork(request.Tag, manifest, targetStart);
        ZoneLoadTotals totals = ApplyLoadWork(work, exactSource: false, request.YOffset);

        return ZoneBundleCommandResult.Ok(
            $"Loaded archive '{request.Tag}' as {work.Count} manifest zone(s) to target start ({targetStart.x},{targetStart.y}) " +
            $"(removed: {totals.Removed}, created: {totals.Created}, terrain: {totals.TerrainApplied}/{work.Count}, mode: SupportFill, yOffset: {Round(request.YOffset)}).");
    }

    private static IEnumerator LoadArchiveManifestAsync(ZoneBundleCommandRequest request, Action<ZoneBundleCommandResult> onComplete, long terrainAssistPeer)
    {
        Vector2i targetStart;
        List<LoadWorkItem> work;
        ZoneBundleManifest manifest;
        int offsetX;
        int offsetZ;
        try
        {
            manifest = ZoneBundleStore.LoadManifest(request.Tag);
            if (manifest.Bundles.Count == 0)
            {
                onComplete(ZoneBundleCommandResult.Fail($"Manifest for tag '{request.Tag}' contains no zone bundles."));
                yield break;
            }

            List<Vector2i> sourceZones = manifest.Bundles.Select(entry => ToVector2i(entry.Zone)).ToList();
            targetStart = ToVector2i(request.TargetZone!);
            int sourceMinX = sourceZones.Min(zone => zone.x);
            int sourceMinZ = sourceZones.Min(zone => zone.y);
            offsetX = targetStart.x - sourceMinX;
            offsetZ = targetStart.y - sourceMinZ;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle async archive load failed: {ex}");
            onComplete(ZoneBundleCommandResult.Fail(ex.Message));
            yield break;
        }

        work = [];
        foreach (ZoneBundleManifestEntry manifestEntry in manifest.Bundles)
        {
            Vector2i sourceZone = ToVector2i(manifestEntry.Zone);
            if (!ZoneBundleStore.TryLoadBundleFromManifestEntry(request.Tag, manifestEntry, out ZoneBundleFile bundle, out string bundleReason))
            {
                _logger.LogError($"Zone bundle async archive load failed: {bundleReason}");
                onComplete(ZoneBundleCommandResult.Fail(bundleReason));
                yield break;
            }

            work.Add(CreateLoadWorkItem(sourceZone, offsetX, offsetZ, bundle));
            yield return null;
        }

        ZoneLoadTotals totals = default;
        TerrainPreparationResult terrainPreparation = default;
        string loadError = "";
        yield return PrepareAndApplyLoadWorkAsync("Zone bundle async archive load failed", request, work, false, request.YOffset, terrainAssistPeer, (loadTotals, preparation, error) =>
        {
            totals = loadTotals;
            terrainPreparation = preparation;
            loadError = error;
        });
        if (!string.IsNullOrWhiteSpace(loadError))
        {
            onComplete(ZoneBundleCommandResult.Fail(loadError));
            yield break;
        }

        onComplete(ZoneBundleCommandResult.Ok(
            $"Loaded archive '{request.Tag}' as {work.Count} manifest zone(s) to target start ({targetStart.x},{targetStart.y}) " +
            $"(removed: {totals.Removed}, created: {totals.Created}, terrain: {totals.TerrainApplied}/{work.Count}, mode: SupportFill{terrainPreparation.Describe()}, yOffset: {Round(request.YOffset)})."));
    }

    private static List<LoadWorkItem> BuildArchiveLoadWork(string tag, ZoneBundleManifest manifest, Vector2i targetStart)
    {
        List<Vector2i> sourceZones = manifest.Bundles.Select(entry => ToVector2i(entry.Zone)).ToList();
        int offsetX = targetStart.x - sourceZones.Min(zone => zone.x);
        int offsetZ = targetStart.y - sourceZones.Min(zone => zone.y);

        List<LoadWorkItem> work = [];
        foreach (ZoneBundleManifestEntry manifestEntry in manifest.Bundles)
        {
            Vector2i sourceZone = ToVector2i(manifestEntry.Zone);
            ZoneBundleFile bundle = ZoneBundleStore.LoadBundleFromManifestEntry(tag, manifestEntry);
            work.Add(CreateLoadWorkItem(sourceZone, offsetX, offsetZ, bundle));
        }

        return work;
    }

    private static LoadWorkItem CreateLoadWorkItem(Vector2i sourceZone, int offsetX, int offsetZ, ZoneBundleFile bundle)
    {
        return new LoadWorkItem(new Vector2i(sourceZone.x + offsetX, sourceZone.y + offsetZ), bundle);
    }

    private static void ValidateLoadReady(IEnumerable<LoadWorkItem> work)
    {
        foreach (LoadWorkItem item in work)
        {
            EnsureSupportFillBundle(item.Bundle);
            if (RequiresTerrainApply(item.Bundle) && !ZoneBundleTerrain.CanApply(item.TargetZone))
            {
                throw new InvalidOperationException(
                    $"Target zone ({item.TargetZone.x},{item.TargetZone.y}) is not loaded for terrain overwrite. Move closer and try again.");
            }
        }
    }

    private static IEnumerator ValidateLoadReadyAsync(IEnumerable<LoadWorkItem> work, Action<string> onComplete, bool allowClientTerrainApply = false)
    {
        foreach (LoadWorkItem item in work.ToList())
        {
            try
            {
                EnsureSupportFillBundle(item.Bundle);
            }
            catch (Exception ex)
            {
                onComplete(ex.Message);
                yield break;
            }

            bool requiresTerrainApply = false;
            yield return RequiresTerrainApplyAsync(item.Bundle, value => requiresTerrainApply = value);
            if (!allowClientTerrainApply && requiresTerrainApply && !ZoneBundleTerrain.CanApply(item.TargetZone))
            {
                onComplete($"Target zone ({item.TargetZone.x},{item.TargetZone.y}) is not loaded for terrain overwrite. Move closer and try again.");
                yield break;
            }

            yield return null;
        }

        onComplete("");
    }

    private static TerrainPlacementContext? CreateTerrainPlacementContext(IEnumerable<LoadWorkItem> work, bool exactSource)
    {
        List<LoadWorkItem> items = work.ToList();
        if (items.Count == 0)
        {
            return null;
        }

        if (exactSource)
        {
            return ZoneBundleTerrain.CreateExactContext(items.Min(item => item.Bundle.SourceBaseY));
        }

        return ZoneBundleTerrain.CreateSupportFillPlacementContext(items.Select(item => new TerrainSupportTarget
        {
            Zone = item.TargetZone,
            SourceBaseY = item.Bundle.SourceBaseY,
            Entries = item.Bundle.Entries,
            ContactsCaptured = item.Bundle.TerrainContactsCaptured,
            Contacts = item.Bundle.TerrainContacts
        }));
    }

    private static TerrainPlacementContext? CreateAndValidateTerrainPlacementContext(IEnumerable<LoadWorkItem> work, bool exactSource, float yOffset)
    {
        List<LoadWorkItem> items = work.ToList();
        TerrainPlacementContext? context = CreateTerrainPlacementContext(items, exactSource);
        ApplyYOffset(context, yOffset);
        ValidateTerrainPlacementContext(items, context);
        return context;
    }

    private static IEnumerator CreateAndValidateTerrainPlacementContextAsync(
        IEnumerable<LoadWorkItem> work,
        bool exactSource,
        float yOffset,
        Action<TerrainPlacementContext?, string> onComplete,
        bool validateTargetTerrain = true)
    {
        List<LoadWorkItem> items = work.ToList();
        TerrainPlacementContext? context = null;
        try
        {
            if (items.Count == 0)
            {
                onComplete(null, "");
                yield break;
            }

            if (exactSource)
            {
                context = CreateTerrainPlacementContext(items, exactSource);
            }
        }
        catch (Exception ex)
        {
            onComplete(null, ex.Message);
            yield break;
        }

        if (!exactSource)
        {
            yield return ZoneBundleTerrain.CreateSupportFillPlacementContextAsync(items.Select(item => new TerrainSupportTarget
            {
                Zone = item.TargetZone,
                SourceBaseY = item.Bundle.SourceBaseY,
                Entries = item.Bundle.Entries,
                ContactsCaptured = item.Bundle.TerrainContactsCaptured,
                Contacts = item.Bundle.TerrainContacts
            }), value => context = value);
        }

        try
        {
            ApplyYOffset(context, yOffset);
            ValidateTerrainPlacementContext(items, context, validateTargetTerrain);
            onComplete(context, "");
        }
        catch (Exception ex)
        {
            onComplete(null, ex.Message);
        }
    }

    private static void ValidateTerrainPlacementContext(IReadOnlyCollection<LoadWorkItem> work, TerrainPlacementContext? terrainContext, bool validateTargetTerrain = true)
    {
        if (!work.Any(item => RequiresTerrainApply(item.Bundle)))
        {
            return;
        }

        if (terrainContext == null)
        {
            throw new InvalidOperationException("Zone bundle terrain support placement could not be resolved. Load aborted before overwriting target zones.");
        }

        if (!validateTargetTerrain)
        {
            if (terrainContext.SupportRelativeHeights.Count == 0)
            {
                throw new InvalidOperationException("Zone bundle terrain support placement produced no support points. Load aborted before overwriting target zones.");
            }

            return;
        }

        bool hasAnySupport = false;
        foreach (LoadWorkItem item in work)
        {
            if (!RequiresTerrainApply(item.Bundle))
            {
                continue;
            }

            if (ZoneBundleTerrain.HasApplicableSupportFill(
                    item.TargetZone,
                    item.Bundle.Entries,
                    item.Bundle.TerrainContacts,
                    item.Bundle.TerrainContactsCaptured,
                    terrainContext))
            {
                hasAnySupport = true;
                break;
            }
        }

        if (!hasAnySupport)
        {
            throw new InvalidOperationException("Zone bundle terrain support placement produced no usable terrain support points. Load aborted before overwriting target zones.");
        }
    }

    private static ZoneLoadTotals ApplyLoadWork(List<LoadWorkItem> work, bool exactSource, float yOffset)
    {
        ValidateLoadReady(work);
        TerrainPlacementContext? terrainContext = CreateAndValidateTerrainPlacementContext(work, exactSource, yOffset);
        return ApplyLoadWork(work, terrainContext, yOffset);
    }

    private static ZoneLoadTotals ApplyLoadWork(IEnumerable<LoadWorkItem> work, TerrainPlacementContext? terrainContext, float yOffset)
    {
        ZoneLoadTotals totals = default;
        foreach (LoadWorkItem item in work)
        {
            ZoneLoadStats stats = ApplyBundleToZone(item.TargetZone, item.Bundle, terrainContext, yOffset);
            totals.Add(stats, stats.TerrainApplied);
        }

        return totals;
    }

    private static IEnumerator PrepareAndApplyLoadWorkAsync(
        string failurePrefix,
        ZoneBundleCommandRequest request,
        IReadOnlyCollection<LoadWorkItem> work,
        bool exactSource,
        float yOffset,
        long terrainAssistPeer,
        Action<ZoneLoadTotals, TerrainPreparationResult, string> onComplete)
    {
        TerrainPlacementContext? terrainContext = null;
        TerrainPreparationResult terrainPreparation = default;
        string prepareError = "";
        yield return PrepareLoadWorkAsync(failurePrefix, request, work, exactSource, yOffset, terrainAssistPeer, (context, preparation, error) =>
        {
            terrainContext = context;
            terrainPreparation = preparation;
            prepareError = error;
        });
        if (!string.IsNullOrWhiteSpace(prepareError))
        {
            onComplete(default, terrainPreparation, prepareError);
            yield break;
        }

        ZoneLoadTotals totals = default;
        yield return ApplyLoadWorkAsync(work, terrainContext, yOffset, terrainPreparation, value => totals = value);
        onComplete(totals, terrainPreparation, "");
    }

    private static IEnumerator PrepareAndApplyLocalLoadWorkAsync(
        string failurePrefix,
        IReadOnlyCollection<LoadWorkItem> work,
        bool exactSource,
        float yOffset,
        Action<ZoneLoadTotals, string> onComplete)
    {
        string readyError = "";
        yield return ValidateLoadReadyAsync(work, value => readyError = value);
        if (!string.IsNullOrWhiteSpace(readyError))
        {
            _logger.LogError($"{failurePrefix}: {readyError}");
            onComplete(default, readyError);
            yield break;
        }

        TerrainPlacementContext? terrainContext = null;
        string contextError = "";
        yield return CreateAndValidateTerrainPlacementContextAsync(work, exactSource, yOffset, (context, error) =>
        {
            terrainContext = context;
            contextError = error;
        });
        if (!string.IsNullOrWhiteSpace(contextError))
        {
            _logger.LogError($"{failurePrefix}: {contextError}");
            onComplete(default, contextError);
            yield break;
        }

        ZoneLoadTotals totals = default;
        TerrainPreparationResult terrainPreparation = TerrainPreparationResult.Completed(false, CountRequiredTerrainTargets(work), 0);
        yield return ApplyLoadWorkAsync(work, terrainContext, yOffset, terrainPreparation, value => totals = value);
        onComplete(totals, "");
    }

    private static IEnumerator PrepareLoadWorkAsync(
        string failurePrefix,
        ZoneBundleCommandRequest request,
        IReadOnlyCollection<LoadWorkItem> work,
        bool exactSource,
        float yOffset,
        long terrainAssistPeer,
        Action<TerrainPlacementContext?, TerrainPreparationResult, string> onComplete)
    {
        string readyError = "";
        bool allowClientTerrainApply = terrainAssistPeer != 0L;
        yield return ValidateLoadReadyAsync(work, value => readyError = value, allowClientTerrainApply);
        if (!string.IsNullOrWhiteSpace(readyError))
        {
            _logger.LogError($"{failurePrefix}: {readyError}");
            onComplete(null, default, readyError);
            yield break;
        }

        TerrainPlacementContext? terrainContext = null;
        string contextError = "";
        yield return CreateAndValidateTerrainPlacementContextAsync(work, exactSource, yOffset, (context, error) =>
        {
            terrainContext = context;
            contextError = error;
        }, validateTargetTerrain: !allowClientTerrainApply);
        if (!string.IsNullOrWhiteSpace(contextError))
        {
            _logger.LogError($"{failurePrefix}: {contextError}");
            onComplete(null, default, contextError);
            yield break;
        }

        TerrainPreparationResult terrainPreparation = default;
        yield return PrepareTerrainForLoadAsync(request, work, terrainContext, terrainAssistPeer, value => terrainPreparation = value);
        if (!terrainPreparation.Success)
        {
            _logger.LogError($"{failurePrefix}: {terrainPreparation.Message}");
            onComplete(null, terrainPreparation, terrainPreparation.Message);
            yield break;
        }

        onComplete(terrainContext, terrainPreparation, "");
    }

    private static IEnumerator ApplyLoadWorkAsync(
        IEnumerable<LoadWorkItem> work,
        TerrainPlacementContext? terrainContext,
        float yOffset,
        TerrainPreparationResult terrainPreparation,
        Action<ZoneLoadTotals> onComplete)
    {
        ZoneLoadTotals totals = default;
        foreach (LoadWorkItem item in work)
        {
            ZoneLoadStats stats = default;
            bool applyTerrain = !terrainPreparation.WasClientPrepared(item.TargetZone);
            yield return ApplyBundleToZoneAsync(item.TargetZone, item.Bundle, terrainContext, yOffset, value => stats = value, applyTerrain);
            bool terrainApplied = stats.TerrainApplied ||
                                  (terrainPreparation.WasClientPrepared(item.TargetZone) && RequiresTerrainApply(item.Bundle));
            totals.Add(stats, terrainApplied);
        }

        onComplete(totals);
    }

    private static IEnumerator PrepareTerrainForLoadAsync(
        ZoneBundleCommandRequest request,
        IReadOnlyCollection<LoadWorkItem> work,
        TerrainPlacementContext? terrainContext,
        long terrainAssistPeer,
        Action<TerrainPreparationResult> onComplete)
    {
        if (!work.Any(item => RequiresTerrainApply(item.Bundle)))
        {
            onComplete(TerrainPreparationResult.Completed(false, 0, 0));
            yield break;
        }

        if (terrainContext == null)
        {
            onComplete(TerrainPreparationResult.Failed("Zone bundle terrain support placement could not be resolved. Load aborted before overwriting target zones."));
            yield break;
        }

        if (AllRequiredTerrainTargetsCanApply(work))
        {
            try
            {
                ValidateTerrainPlacementContext(work, terrainContext);
                onComplete(TerrainPreparationResult.Completed(false, CountRequiredTerrainTargets(work), 0));
            }
            catch (Exception ex)
            {
                onComplete(TerrainPreparationResult.Failed(ex.Message));
            }

            yield break;
        }

        HashSet<Vector2i> clientPreparedZones = [];
        foreach (Vector2i zone in RequiredTerrainTargetZones(work))
        {
            if (ZoneBundleTerrain.CanApply(zone))
            {
                continue;
            }

            ZoneBundleClientTerrainApplyResponse? response = null;
            string terrainApplyFailure = "";
            foreach (long witnessPeer in GetTerrainWitnessCandidates(zone, terrainAssistPeer))
            {
                yield return RequestClientTerrainApplyAsync(witnessPeer, request, [zone], terrainContext, value => response = value);
                if (response is { Success: true })
                {
                    break;
                }

                terrainApplyFailure = response?.Message ?? "client terrain apply did not return a response";
            }

            if (response is not { Success: true })
            {
                onComplete(TerrainPreparationResult.Failed(string.IsNullOrWhiteSpace(terrainApplyFailure)
                    ? $"Target zone ({zone.x},{zone.y}) is not loaded for terrain overwrite and no ZoneSavior terrain witness is nearby."
                    : terrainApplyFailure));
                yield break;
            }

            clientPreparedZones.Add(zone);
        }

        onComplete(TerrainPreparationResult.Completed(clientPreparedZones, CountRequiredTerrainTargets(work), clientPreparedZones.Count));
    }

    private static IEnumerator RequestClientTerrainApplyAsync(
        long terrainAssistPeer,
        ZoneBundleCommandRequest commandRequest,
        IReadOnlyCollection<Vector2i> targetZones,
        TerrainPlacementContext terrainContext,
        Action<ZoneBundleClientTerrainApplyResponse?> onComplete)
    {
        string requestId = System.Guid.NewGuid().ToString("N");
        List<ZoneBundleZone> targetZoneModels = targetZones
            .Distinct()
            .Select(ToModel)
            .ToList();

        ZoneBundleClientTerrainApplyRequest request = new()
        {
            RequestId = requestId,
            Operation = commandRequest.Operation,
            Tag = commandRequest.Tag,
            Context = terrainContext,
            TargetZones = targetZoneModels
        };

        ClientTerrainApplyResponses.Remove(requestId);
        ZPackage package = new();
        package.Write(ZoneBundleSerialization.Serialize(request));
        ZRoutedRpc.instance.InvokeRoutedRPC(terrainAssistPeer, ClientTerrainApplyRequestRpcName, package);

        float deadline = Time.realtimeSinceStartup + ClientTerrainApplyTimeoutSeconds;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (ClientTerrainApplyResponses.TryGetValue(requestId, out ZoneBundleClientTerrainApplyResponse response))
            {
                ClientTerrainApplyResponses.Remove(requestId);
                onComplete(response);
                yield break;
            }

            yield return null;
        }

        ClientTerrainApplyResponses.Remove(requestId);
        onComplete(new ZoneBundleClientTerrainApplyResponse
        {
            RequestId = requestId,
            Success = false,
            Message = $"Client terrain apply timed out after {ClientTerrainApplyTimeoutSeconds:0} seconds."
        });
    }

    private static IEnumerator RequestClientTerrainCaptureAsync(
        long terrainAssistPeer,
        string tag,
        Vector2i zone,
        ZoneBundleFile bundle,
        Action<ZoneBundleClientTerrainCaptureResponse?> onComplete)
    {
        string requestId = System.Guid.NewGuid().ToString("N");
        ZoneBundleClientTerrainCaptureRequest request = new()
        {
            RequestId = requestId,
            Tag = tag,
            Zone = ToModel(zone),
            SourceBaseY = bundle.SourceBaseY,
            Entries = CreateTerrainContactCaptureEntries(bundle)
        };

        if (request.Entries.Count == 0)
        {
            onComplete(new ZoneBundleClientTerrainCaptureResponse
            {
                RequestId = requestId,
                Success = true,
                ContactsCaptured = true,
                Message = "No WearNTear entries require terrain contacts."
            });
            yield break;
        }

        ClientTerrainCaptureResponses.Remove(requestId);
        ZPackage package = new();
        package.Write(ZoneBundleSerialization.Serialize(request));
        ZRoutedRpc.instance.InvokeRoutedRPC(terrainAssistPeer, ClientTerrainCaptureRequestRpcName, package);

        float deadline = Time.realtimeSinceStartup + ClientTerrainApplyTimeoutSeconds;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (ClientTerrainCaptureResponses.TryGetValue(requestId, out ZoneBundleClientTerrainCaptureResponse response))
            {
                ClientTerrainCaptureResponses.Remove(requestId);
                onComplete(response);
                yield break;
            }

            yield return null;
        }

        ClientTerrainCaptureResponses.Remove(requestId);
        onComplete(new ZoneBundleClientTerrainCaptureResponse
        {
            RequestId = requestId,
            Success = false,
            Message = $"Client terrain capture timed out after {ClientTerrainApplyTimeoutSeconds:0} seconds."
        });
    }

    private static ZoneBundleClientTerrainCaptureResponse CaptureClientTerrainContacts(ZoneBundleClientTerrainCaptureRequest request)
    {
        ZoneBundleClientTerrainCaptureResponse response = new()
        {
            RequestId = request.RequestId
        };

        try
        {
            Vector2i zone = ToVector2i(request.Zone);
            List<ZoneBundleTerrainContact> contacts = ZoneBundleTerrain.CaptureSupportContacts(
                zone,
                request.SourceBaseY,
                request.Entries,
                out bool contactsCaptured);

            response.Success = contactsCaptured;
            response.ContactsCaptured = contactsCaptured;
            response.Contacts = contacts;
            response.Message = contactsCaptured
                ? $"Client captured {contacts.Count} terrain contact(s) for zone ({zone.x},{zone.y})."
                : $"Client source zone ({zone.x},{zone.y}) is not loaded for terrain contact capture.";
        }
        catch (Exception ex)
        {
            response.Success = false;
            response.Message = $"Client terrain contact capture failed: {ex.Message}";
        }

        return response;
    }

    private static IEnumerator ApplyClientTerrainRequestAsync(long sender, ZoneBundleClientTerrainApplyRequest request)
    {
        ZoneBundleClientTerrainApplyResponse response = new()
        {
            RequestId = request.RequestId,
            TargetZones = request.TargetZones.Count
        };

        if (request.Context == null)
        {
            response.Success = false;
            response.Message = "Client terrain apply failed: missing terrain context.";
            SendClientTerrainApplyResponse(sender, response);
            yield break;
        }

        if (request.Context.SupportRelativeHeights == null || request.Context.SupportRelativeHeights.Count == 0)
        {
            response.Success = false;
            response.Message = "Client terrain apply failed: terrain context has no support points.";
            SendClientTerrainApplyResponse(sender, response);
            yield break;
        }

        foreach (ZoneBundleZone model in request.TargetZones)
        {
            Vector2i zone = ToVector2i(model);
            try
            {
                if (!ZoneBundleTerrain.CanApply(zone))
                {
                    response.Success = false;
                    response.Message = $"Client target zone ({zone.x},{zone.y}) is not loaded for terrain overwrite. Move the admin client into the target area and try again.";
                    SendClientTerrainApplyResponse(sender, response);
                    yield break;
                }

                if (!ZoneBundleTerrain.HasApplicableSupportFill(
                        zone,
                        Enumerable.Empty<ZoneBundleEntry>(),
                        Enumerable.Empty<ZoneBundleTerrainContact>(),
                        contactsCaptured: false,
                        request.Context))
                {
                    response.Success = false;
                    response.Message = $"Client target zone ({zone.x},{zone.y}) has no applicable terrain support points.";
                    SendClientTerrainApplyResponse(sender, response);
                    yield break;
                }
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = $"Client terrain validation failed for zone ({zone.x},{zone.y}): {ex.Message}";
                SendClientTerrainApplyResponse(sender, response);
                yield break;
            }

            bool changed;
            try
            {
                changed = ZoneBundleTerrain.ApplySupportFill(
                    zone,
                    Enumerable.Empty<ZoneBundleEntry>(),
                    Enumerable.Empty<ZoneBundleTerrainContact>(),
                    contactsCaptured: false,
                    request.Context);
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Message = $"Client terrain apply failed for zone ({zone.x},{zone.y}): {ex.Message}";
                SendClientTerrainApplyResponse(sender, response);
                yield break;
            }

            if (changed)
            {
                response.ChangedZones++;
            }

            yield return null;
        }

        response.Success = true;
        response.Message = $"Client applied terrain support for {request.TargetZones.Count} target zone(s).";
        SendClientTerrainApplyResponse(sender, response);
    }

    private static bool AllRequiredTerrainTargetsCanApply(IEnumerable<LoadWorkItem> work)
    {
        return work
            .Where(item => RequiresTerrainApply(item.Bundle))
            .All(item => ZoneBundleTerrain.CanApply(item.TargetZone));
    }

    private static int CountRequiredTerrainTargets(IEnumerable<LoadWorkItem> work)
    {
        return RequiredTerrainTargetZones(work).Count;
    }

    private static List<Vector2i> RequiredTerrainTargetZones(IEnumerable<LoadWorkItem> work)
    {
        return work
            .Where(item => RequiresTerrainApply(item.Bundle))
            .Select(item => item.TargetZone)
            .Distinct()
            .ToList();
    }

    private static List<long> GetTerrainWitnessCandidates(Vector2i zone, long preferredPeer)
    {
        if (ZNet.instance == null)
        {
            return [];
        }

        List<TerrainWitnessCandidate> candidates = [];
        HashSet<long> seen = [];
        AddTerrainWitnessCandidate(candidates, seen, zone, preferredPeer, preferred: true);

        foreach (long peerId in TerrainWitnessPeers.ToList())
        {
            AddTerrainWitnessCandidate(candidates, seen, zone, peerId, preferred: false);
        }

        return candidates
            .OrderBy(candidate => candidate.Preferred ? 0 : 1)
            .ThenBy(candidate => candidate.DistanceSqr)
            .Select(candidate => candidate.PeerId)
            .ToList();
    }

    private static void AddTerrainWitnessCandidate(List<TerrainWitnessCandidate> candidates, HashSet<long> seen, Vector2i zone, long peerId, bool preferred)
    {
        if (peerId == 0L || !seen.Add(peerId) || ZNet.instance == null)
        {
            return;
        }

        ZNetPeer peer = ZNet.instance.GetPeer(peerId);
        if (peer == null || !peer.IsReady())
        {
            TerrainWitnessPeers.Remove(peerId);
            return;
        }

        if (!TryGetPeerZoneDistanceSqr(peer, zone, out float distanceSqr))
        {
            return;
        }

        float maxDistanceSqr = TerrainWitnessSearchRadius * TerrainWitnessSearchRadius;
        if (distanceSqr > maxDistanceSqr)
        {
            return;
        }

        candidates.Add(new TerrainWitnessCandidate(peerId, distanceSqr, preferred));
    }

    private static bool TryGetPeerZoneDistanceSqr(ZNetPeer peer, Vector2i zone, out float distanceSqr)
    {
        distanceSqr = float.PositiveInfinity;
        Vector3 peerPosition = peer.m_refPos;
        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        float dx = peerPosition.x - zoneCenter.x;
        float dz = peerPosition.z - zoneCenter.z;
        distanceSqr = dx * dx + dz * dz;
        return true;
    }

    private static List<ZoneBundleEntry> CreateTerrainContactCaptureEntries(ZoneBundleFile bundle)
    {
        List<ZoneBundleEntry> entries = [];
        foreach (ZoneBundleEntry entry in bundle.Entries)
        {
            if (string.Equals(entry.Kind, "item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            if (!prefab || prefab.GetComponent<WearNTear>() == null)
            {
                continue;
            }

            entries.Add(new ZoneBundleEntry
            {
                SaveId = entry.SaveId,
                Kind = entry.Kind,
                Prefab = entry.Prefab,
                LocalPos = entry.LocalPos,
                Rot = entry.Rot,
                Scale = entry.Scale
            });
        }

        return entries;
    }

    private static bool IsSupportFillBundle(ZoneBundleFile bundle)
    {
        return string.Equals(bundle.TerrainMode, ZoneBundleTerrain.SupportFillMode, StringComparison.Ordinal);
    }

    private static bool UsesRelativeY(ZoneBundleFile bundle)
    {
        return IsSupportFillBundle(bundle);
    }

    private static bool RequiresTerrainApply(ZoneBundleFile bundle)
    {
        if (!IsSupportFillBundle(bundle))
        {
            return false;
        }

        return HasSavedTerrainContacts(bundle) || HasWearNTearEntries(bundle);
    }

    private static IEnumerator RequiresTerrainApplyAsync(ZoneBundleFile bundle, Action<bool> onComplete)
    {
        if (!IsSupportFillBundle(bundle))
        {
            onComplete(false);
            yield break;
        }

        if (HasSavedTerrainContacts(bundle))
        {
            onComplete(true);
            yield break;
        }

        bool hasWearNTearEntries = false;
        yield return HasWearNTearEntriesAsync(bundle, value => hasWearNTearEntries = value);
        onComplete(hasWearNTearEntries);
    }

    private static IEnumerator HasWearNTearEntriesAsync(ZoneBundleFile bundle, Action<bool> onComplete)
    {
        int processedSinceYield = 0;
        foreach (ZoneBundleEntry entry in bundle.Entries)
        {
            if (!string.Equals(entry.Kind, "item", StringComparison.OrdinalIgnoreCase))
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
                if (prefab && prefab.GetComponent<WearNTear>() != null)
                {
                    onComplete(true);
                    yield break;
                }
            }

            processedSinceYield++;
            if (processedSinceYield >= CaptureBatchSize)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        onComplete(false);
    }

    private static void EnsureSupportFillBundle(ZoneBundleFile bundle)
    {
        if (IsSupportFillBundle(bundle))
        {
            return;
        }

        string mode = string.IsNullOrWhiteSpace(bundle.TerrainMode) ? "unknown" : bundle.TerrainMode;
        throw new InvalidOperationException($"Unsupported zone bundle terrain mode '{mode}'. Re-save the zone with the current SupportFill format.");
    }

    private static ZoneBundleFile CaptureBundle(Vector2i zone, string tag, ZoneBundleTerrain.TerrainSourceAnchor sourceAnchor, out int entries, out int monsters, out ZoneBundleTerrainCaptureState terrainState)
    {
        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        bool useRelativePlacement = !float.IsNaN(sourceAnchor.BaseWorldY);
        List<ZoneBundleEntry> zoneEntries = new();
        Dictionary<long, string> creatorNames = [];
        List<ZDO> objects = new();
        ZDOMan.instance.FindObjects(zone, objects);

        int staticCount = 0;
        int monsterCount = 0;
        foreach (ZDO zdo in objects)
        {
            if (TryCreateBundleEntry(
                    zdo,
                    zone,
                    zoneCenter,
                    sourceAnchor,
                    useRelativePlacement,
                    creatorNames,
                    ref staticCount,
                    ref monsterCount,
                    out ZoneBundleEntry entry))
            {
                zoneEntries.Add(entry);
            }
        }

        List<ZoneBundleTerrainContact> terrainContacts = ZoneBundleTerrain.CaptureSupportContacts(zone, sourceAnchor.BaseWorldY, zoneEntries, out bool contactsCaptured);
        terrainState = GetTerrainCaptureState(contactsCaptured, terrainContacts.Count);

        entries = zoneEntries.Count;
        monsters = monsterCount;
        return CreateCapturedBundle(zone, tag, sourceAnchor, useRelativePlacement, zoneEntries, creatorNames, terrainContacts, contactsCaptured, terrainState);
    }

    private static IEnumerator CaptureBundleAsync(Vector2i zone, string tag, ZoneBundleTerrain.TerrainSourceAnchor sourceAnchor, Action<CaptureBundleResult> onComplete)
    {
        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        bool useRelativePlacement = !float.IsNaN(sourceAnchor.BaseWorldY);
        List<ZoneBundleEntry> zoneEntries = [];
        Dictionary<long, string> creatorNames = [];
        List<ZDO> objects = [];
        try
        {
            ZDOMan.instance.FindObjects(zone, objects);
        }
        catch (Exception ex)
        {
            onComplete(CaptureBundleResult.Failed(ex.Message));
            yield break;
        }

        int staticCount = 0;
        int monsterCount = 0;
        int processedSinceYield = 0;
        foreach (ZDO zdo in objects)
        {
            try
            {
                if (TryCreateBundleEntry(
                        zdo,
                        zone,
                        zoneCenter,
                        sourceAnchor,
                        useRelativePlacement,
                        creatorNames,
                        ref staticCount,
                        ref monsterCount,
                        out ZoneBundleEntry entry))
                {
                    zoneEntries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                onComplete(CaptureBundleResult.Failed(ex.Message));
                yield break;
            }

            processedSinceYield++;
            if (processedSinceYield >= CaptureBatchSize)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        yield return null;

        try
        {
            List<ZoneBundleTerrainContact> terrainContacts = ZoneBundleTerrain.CaptureSupportContacts(zone, sourceAnchor.BaseWorldY, zoneEntries, out bool contactsCaptured);
            ZoneBundleTerrainCaptureState terrainState = GetTerrainCaptureState(contactsCaptured, terrainContacts.Count);
            ZoneBundleFile bundle = CreateCapturedBundle(
                zone,
                tag,
                sourceAnchor,
                useRelativePlacement,
                zoneEntries,
                creatorNames,
                terrainContacts,
                contactsCaptured,
                terrainState);

            onComplete(CaptureBundleResult.Completed(bundle, zoneEntries.Count, monsterCount, terrainState));
        }
        catch (Exception ex)
        {
            onComplete(CaptureBundleResult.Failed(ex.Message));
        }
    }

    private static bool TryCreateBundleEntry(
        ZDO zdo,
        Vector2i zone,
        Vector3 zoneCenter,
        ZoneBundleTerrain.TerrainSourceAnchor sourceAnchor,
        bool useRelativePlacement,
        Dictionary<long, string> creatorNames,
        ref int staticCount,
        ref int monsterCount,
        out ZoneBundleEntry entry)
    {
        entry = null!;
        if (zdo == null || !zdo.IsValid())
        {
            return false;
        }

        if (!TryClassify(zdo, out SaveEntryKind kind, out GameObject prefab))
        {
            return false;
        }

        bool wearNTear = prefab.GetComponent<WearNTear>() != null;
        bool tamedMonster = kind == SaveEntryKind.Monster;
        long creatorPlayerId = wearNTear ? zdo.GetLong(ZDOVars.s_creator, 0L) : 0L;
        string creatorName = wearNTear ? zdo.GetString(ZDOVars.s_creatorName, "") : "";
        AddCreatorPlayer(creatorNames, creatorPlayerId, creatorName);

        if (wearNTear && !ZoneSaviorBuildRecipeRules.HasBuildRecipe(prefab))
        {
            return false;
        }

        if (!ZoneBundleTerrain.IsSupportWearNTear(zdo, zone, out _) && !tamedMonster)
        {
            return false;
        }

        DataEntry data = new(zdo);
        string sanitize = kind switch
        {
            SaveEntryKind.Monster => MonsterSanitize,
            _ when wearNTear => WearNTearSanitize,
            _ => ""
        };
        SanitizeForSave(kind, data, sanitize);

        Vector3 worldPosition = zdo.m_position;
        Quaternion rotation = zdo.GetRotation();
        Vector3 scale = ReadScale(zdo, prefab);

        entry = new ZoneBundleEntry
        {
            SaveId = kind switch
            {
                SaveEntryKind.Monster => $"m_{++monsterCount:D4}",
                _ => $"s_{++staticCount:D4}"
            },
            Kind = kind switch
            {
                SaveEntryKind.Monster => "monster",
                _ => "static"
            },
            Prefab = Utils.GetPrefabName(prefab),
            LocalPos =
            [
                Round(worldPosition.x - zoneCenter.x),
                Round(useRelativePlacement ? worldPosition.y - sourceAnchor.BaseWorldY : worldPosition.y),
                Round(worldPosition.z - zoneCenter.z)
            ],
            Rot =
            [
                Round(rotation.x),
                Round(rotation.y),
                Round(rotation.z),
                Round(rotation.w)
            ],
            Scale =
            [
                Round(scale.x),
                Round(scale.y),
                Round(scale.z)
            ],
            CreatorPlayerId = creatorPlayerId,
            CreatorName = NormalizeCreatorString(creatorName),
            Data = data.GetBase64(EmptyParameters),
            Sanitize = sanitize
        };
        return true;
    }

    private static ZoneBundleFile CreateCapturedBundle(
        Vector2i zone,
        string tag,
        ZoneBundleTerrain.TerrainSourceAnchor sourceAnchor,
        bool useRelativePlacement,
        List<ZoneBundleEntry> zoneEntries,
        Dictionary<long, string> creatorNames,
        List<ZoneBundleTerrainContact> terrainContacts,
        bool contactsCaptured,
        ZoneBundleTerrainCaptureState terrainState)
    {
        return new ZoneBundleFile
        {
            Tag = tag,
            SourceZone = ToModel(zone),
            TerrainMode = ZoneBundleTerrain.SupportFillMode,
            SourceBaseY = useRelativePlacement ? sourceAnchor.BaseWorldY : 0f,
            TerrainCaptureState = terrainState,
            TerrainContactsCaptured = contactsCaptured,
            TerrainContacts = terrainContacts,
            SourceZoneCreators = BuildSourceZoneCreators(creatorNames),
            Entries = zoneEntries
                .OrderBy(entry => entry.Kind, StringComparer.Ordinal)
                .ThenBy(entry => entry.Prefab, StringComparer.Ordinal)
                .ThenBy(entry => entry.LocalPos[0])
                .ThenBy(entry => entry.LocalPos[2])
                .ThenBy(entry => entry.LocalPos[1])
                .ToList()
        };
    }

    private static ZoneBundleTerrainCaptureState GetTerrainCaptureState(bool contactsCaptured, int contactCount)
    {
        if (!contactsCaptured)
        {
            return ZoneBundleTerrainCaptureState.NotLoaded;
        }

        return contactCount > 0 ? ZoneBundleTerrainCaptureState.Contacts : ZoneBundleTerrainCaptureState.LoadedNoContacts;
    }

    private static bool HasSavedTerrainContacts(ZoneBundleFile bundle)
    {
        return bundle.TerrainContactsCaptured && bundle.TerrainContacts.Count > 0;
    }

    private static bool HasWearNTearEntries(ZoneBundleFile bundle)
    {
        foreach (ZoneBundleEntry entry in bundle.Entries)
        {
            if (string.Equals(entry.Kind, "item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            if (prefab && prefab.GetComponent<WearNTear>() != null)
            {
                return true;
            }
        }

        return false;
    }

    private static ZoneLoadStats ApplyBundleToZone(Vector2i targetZone, ZoneBundleFile bundle, TerrainPlacementContext? terrainContext, float yOffset)
    {
        int removed = ClearTargetZone(targetZone);
        bool terrainApplied = false;

        if (terrainContext != null)
        {
            terrainApplied = ZoneBundleTerrain.ApplySupportFill(
                targetZone,
                bundle.Entries,
                bundle.TerrainContacts,
                bundle.TerrainContactsCaptured,
                terrainContext);
        }

        int created = 0;
        Vector3 zoneCenter = ZoneSystem.GetZonePos(targetZone);
        foreach (ZoneBundleEntry entry in bundle.Entries)
        {
            if (!TryCreateLoadedZdo(entry, zoneCenter, bundle, terrainContext, yOffset, out ZDO? zdo) || zdo == null)
            {
                continue;
            }

            ZNetScene.instance.CreateObject(zdo);
            created++;
        }

        return new ZoneLoadStats(removed, created, terrainApplied);
    }

    private static IEnumerator ApplyBundleToZoneAsync(
        Vector2i targetZone,
        ZoneBundleFile bundle,
        TerrainPlacementContext? terrainContext,
        float yOffset,
        Action<ZoneLoadStats> onComplete,
        bool applyTerrain = true)
    {
        int removed = 0;
        yield return ClearTargetZoneAsync(targetZone, value => removed = value);
        yield return null;

        bool terrainApplied = false;
        if (applyTerrain && terrainContext != null)
        {
            yield return ZoneBundleTerrain.ApplySupportFillAsync(
                targetZone,
                bundle.Entries,
                bundle.TerrainContacts,
                bundle.TerrainContactsCaptured,
                terrainContext,
                result => terrainApplied = result);
        }

        int created = 0;
        int processedSinceYield = 0;
        Vector3 zoneCenter = ZoneSystem.GetZonePos(targetZone);
        foreach (ZoneBundleEntry entry in bundle.Entries)
        {
            if (TryCreateLoadedZdo(entry, zoneCenter, bundle, terrainContext, yOffset, out ZDO? zdo) && zdo != null)
            {
                ZNetScene.instance.CreateObject(zdo);
                created++;
            }

            processedSinceYield++;
            if (processedSinceYield >= CaptureBatchSize)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        onComplete(new ZoneLoadStats(removed, created, terrainApplied));
    }

    private static bool TryCreateLoadedZdo(
        ZoneBundleEntry entry,
        Vector3 zoneCenter,
        ZoneBundleFile bundle,
        TerrainPlacementContext? terrainContext,
        float yOffset,
        out ZDO? zdo)
    {
        zdo = null;
        if (string.Equals(entry.Kind, "item", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
        if (!prefab)
        {
            _logger.LogWarning($"Missing prefab '{entry.Prefab}' while loading zone bundle.");
            return false;
        }

        if (prefab.GetComponent<ItemDrop>())
        {
            return false;
        }

        float baseWorldY = terrainContext?.BaseWorldY ?? bundle.SourceBaseY + yOffset;
        float worldY = UsesRelativeY(bundle) ? baseWorldY + entry.LocalPos[1] : entry.LocalPos[1] + yOffset;
        Vector3 position = new(zoneCenter.x + entry.LocalPos[0], worldY, zoneCenter.z + entry.LocalPos[2]);
        Quaternion rotation = new(entry.Rot[0], entry.Rot[1], entry.Rot[2], entry.Rot[3]);
        Vector3 scale = new(entry.Scale[0], entry.Scale[1], entry.Scale[2]);

        DataEntry data = string.IsNullOrEmpty(entry.Data) ? new DataEntry() : new DataEntry(entry.Data);
        SanitizeForLoad(entry, prefab, data);

        zdo = DataHelper.Init(prefab, position, rotation, scale, data, EmptyParameters);
        return zdo != null;
    }

}


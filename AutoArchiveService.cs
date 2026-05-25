using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ZoneSavior;

internal static class AutoArchiveService
{
    private static readonly TimeSpan TrackInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);

    private static ManualLogSource _logger = null!;
    private static bool _initialized;
    private static bool _scanRunning;
    private static DateTime _lastTrackUtc = DateTime.MinValue;
    private static DateTime _lastFlushUtc = DateTime.MinValue;
    private static Coroutine? _scanCoroutine;

    public static bool IsScanRunning => _scanRunning;

    public static void Initialize(ManualLogSource logger)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _logger = logger;
        PlayerActivityTracker.Initialize(logger);
        AutoArchiveScanner.Initialize(logger);
    }

    public static void Update()
    {
        if (!IsServerReady())
        {
            return;
        }

        DateTime utcNow = DateTime.UtcNow;
        if (utcNow - _lastTrackUtc >= TrackInterval)
        {
            _lastTrackUtc = utcNow;
            PlayerActivityTracker.TrackOnlinePlayers(utcNow);
        }

        if (utcNow - _lastFlushUtc >= FlushInterval)
        {
            _lastFlushUtc = utcNow;
            AutoArchiveStore.Flush();
        }

        if (!AutoArchiveConfig.Enabled || _scanRunning)
        {
            return;
        }

        TimeSpan scanInterval = TimeSpan.FromMinutes(AutoArchiveConfig.ScanIntervalMinutes);
        DateTime lastAutoScanUtc = DateTime.SpecifyKind(AutoArchiveStore.State.LastAutoScanUtc, DateTimeKind.Utc);
        if (lastAutoScanUtc != DateTime.MinValue && utcNow - lastAutoScanUtc < scanInterval)
        {
            return;
        }

        QueueScan(new AutoArchiveScanOptions
        {
            Manual = false,
            DryRun = AutoArchiveConfig.DryRun,
            ResetAfterSave = AutoArchiveConfig.ResetAfterSave
        });
    }

    public static bool QueueManualScan(bool? dryRunOverride = null, bool? resetAfterSaveOverride = null, IEnumerable<long>? targetPlayerIds = null)
    {
        if (!IsServerReady() || _scanRunning)
        {
            return false;
        }

        QueueScan(new AutoArchiveScanOptions
        {
            Manual = true,
            DryRun = dryRunOverride ?? AutoArchiveConfig.DryRun,
            ResetAfterSave = resetAfterSaveOverride ?? AutoArchiveConfig.ResetAfterSave,
            TargetPlayerIds = targetPlayerIds == null ? [] : new List<long>(targetPlayerIds)
        });
        return true;
    }

    public static void Shutdown()
    {
        if (_scanCoroutine != null && ZoneSaviorPlugin.Instance != null)
        {
            ZoneSaviorPlugin.Instance.StopCoroutine(_scanCoroutine);
            _scanCoroutine = null;
        }

        _scanRunning = false;
    }

    private static void QueueScan(AutoArchiveScanOptions options)
    {
        _scanRunning = true;
        _logger.LogInfo(
            $"Starting auto archive scan (manual: {options.Manual}, dry run: {options.DryRun}, reset: {options.ResetAfterSave}, targets: {options.TargetPlayerIds.Count}).");
        _scanCoroutine = ZoneSaviorPlugin.Instance.StartCoroutine(RunScan(options));
    }

    private static IEnumerator RunScan(AutoArchiveScanOptions options)
    {
        ArchiveRunRecord? completed = null;
        yield return AutoArchiveScanner.Run(options, run => completed = run);

        if (completed != null)
        {
            AutoArchiveStore.RecordRun(completed);
            AutoArchiveStore.Flush(force: true);
            _logger.LogInfo(
                $"Auto archive scan finished: {completed.CandidateZones} candidate zone(s), {completed.ProcessedZones} processed zone(s), {completed.Clusters.Count} cluster record(s).");
        }

        _scanRunning = false;
        _scanCoroutine = null;
    }

    private static bool IsServerReady()
    {
        return ZoneSaviorPlugin.Instance != null &&
               ZNet.instance != null &&
               ZNet.instance.IsServer() &&
               ZDOMan.instance != null &&
               ZNetScene.instance != null &&
               ZoneSystem.instance != null;
    }
}


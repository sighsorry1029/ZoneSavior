using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
    private static int _scanOperationToken;

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

        _ = QueueScan(new AutoArchiveScanOptions
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

        return QueueScan(new AutoArchiveScanOptions
        {
            Manual = true,
            DryRun = dryRunOverride ?? AutoArchiveConfig.DryRun,
            ResetAfterSave = resetAfterSaveOverride ?? AutoArchiveConfig.ResetAfterSave,
            TargetPlayerIds = targetPlayerIds == null ? [] : new List<long>(targetPlayerIds)
        });
    }

    public static void Shutdown()
    {
        if (_scanCoroutine != null && ZoneSaviorPlugin.Instance != null)
        {
            ZoneSaviorPlugin.Instance.StopCoroutine(_scanCoroutine);
            _scanCoroutine = null;
        }

        ZoneBundleCommands.EndOperation(_scanOperationToken);
        _scanOperationToken = 0;
        _scanRunning = false;
    }

    private static bool QueueScan(AutoArchiveScanOptions options)
    {
        if (!ZoneBundleCommands.TryBeginOperation("auto archive scan", out int operationToken, out _))
        {
            return false;
        }

        _scanOperationToken = operationToken;
        _scanRunning = true;
        _logger.LogInfo(
            $"Starting auto archive scan (manual: {options.Manual}, dry run: {options.DryRun}, reset: {options.ResetAfterSave}, targets: {options.TargetPlayerIds.Count}).");
        try
        {
            _scanCoroutine = ZoneSaviorPlugin.Instance.StartCoroutine(RunScan(options, operationToken));
            if (_scanCoroutine == null)
            {
                throw new InvalidOperationException("Unity did not start the auto archive coroutine.");
            }

            return true;
        }
        catch (Exception ex)
        {
            ZoneBundleCommands.EndOperation(operationToken);
            _scanOperationToken = 0;
            _scanRunning = false;
            _scanCoroutine = null;
            _logger.LogError($"Auto archive scan could not start: {ex}");
            try
            {
                AutoArchiveStore.RecordRun(CreateFailedRun(
                    options,
                    DateTime.UtcNow,
                    $"Auto archive scan could not start: {ex.Message}"));
                AutoArchiveStore.Flush(force: true);
            }
            catch (Exception persistenceException)
            {
                _logger.LogError($"Failed to record auto archive start failure: {persistenceException}");
            }

            return false;
        }
    }

    private static IEnumerator RunScan(AutoArchiveScanOptions options, int operationToken)
    {
        ArchiveRunRecord? completed = null;
        Exception? failure = null;
        try
        {
            yield return ZoneSaviorCoroutines.RunSafely(
                AutoArchiveScanner.Run(options, run => completed = run),
                exception => failure = exception);

            if (failure != null || completed == null)
            {
                DateTime failedAt = DateTime.UtcNow;
                string failureMessage = failure != null
                    ? $"Auto archive scan failed: {failure.Message}"
                    : "Auto archive scanner did not return a result.";
                completed = CreateFailedRun(options, failedAt, failureMessage);
                _logger.LogError(failure != null
                    ? $"Auto archive scan failed unexpectedly: {failure}"
                    : failureMessage);
            }

            AutoArchiveStore.RecordRun(completed);
            AutoArchiveStore.Flush(force: true);
            _logger.LogInfo(
                $"Auto archive scan finished: {completed.CandidateZones} candidate zone(s), {completed.ProcessedZones} processed zone(s), {completed.Clusters.Count} cluster record(s).");
        }
        finally
        {
            ZoneBundleCommands.EndOperation(operationToken);
            if (_scanOperationToken == operationToken)
            {
                _scanOperationToken = 0;
            }

            _scanRunning = false;
            _scanCoroutine = null;
        }
    }

    private static ArchiveRunRecord CreateFailedRun(
        AutoArchiveScanOptions options,
        DateTime failedAt,
        string failureMessage)
    {
        return new ArchiveRunRecord
        {
            RunId = failedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture),
            CreatedUtc = failedAt,
            Manual = options.Manual,
            DryRun = options.DryRun,
            ResetAfterSave = options.ResetAfterSave,
            TargetPlayerIds = new List<long>(options.TargetPlayerIds),
            Messages = [failureMessage]
        };
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


using BepInEx.Logging;

namespace ZoneSavior;

internal static class ZoneSaviorFeatureBootstrap
{
    public static void Initialize(ManualLogSource logger)
    {
        ZoneSaviorInputBlockers.Initialize(logger);
        ZonePieceCounter.Initialize(logger);
        ZoneLimitConfiguration.Initialize(ZoneSaviorPlugin.ConfigSync, logger);
        ZoneBundleCommands.Initialize(logger);
        ZoneBundleSupportGrace.Initialize(logger);
        AutoArchiveStore.Initialize(logger);
        AutoArchiveService.Initialize(logger);
        AutoArchiveCommands.Initialize(logger);
    }

    public static void Update()
    {
        ZoneBundleCommands.RegisterRpcs();
        ZoneBundleSupportGrace.Update();
        AutoArchiveCommands.RegisterRpcs();
        CreatorNameLookupRpc.Register();
        AutoArchiveService.Update();
        ZoneBoundaryOverlay.Update();
    }

    public static void Shutdown()
    {
        ZonePieceCounter.Clear();
        ZoneBundleSupportGrace.Shutdown();
        ZoneBoundaryOverlay.Shutdown();
        AutoArchiveService.Shutdown();
        CreatorNameLookupRpc.ClearRateLimits();
        AutoArchiveStore.Flush(force: true);
    }

    public static void ReloadZoneRulesFromDisk()
    {
        ZoneLimitConfiguration.ReloadFromDisk();
    }

    public static bool TryReloadActivityFromDisk(out string message)
    {
        return AutoArchiveStore.TryReloadFromDiskIfSafe(AutoArchiveService.IsScanRunning, out message);
    }
}


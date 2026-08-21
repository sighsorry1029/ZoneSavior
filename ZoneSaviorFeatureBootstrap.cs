using BepInEx.Logging;
using HarmonyLib;

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
        AdminTerrainTool.Initialize(logger);
    }

    public static void InitializeCompat(ManualLogSource logger, Harmony harmony)
    {
        ZoneSaviorExpandWorldDataCompat.Initialize(logger, harmony);
        AdminTerrainTool.InitializeCompat(harmony);
        VeiledRecipesCompat.Initialize(logger);
    }

    public static void Update()
    {
        ZoneBundleCommands.RegisterRpcs();
        ZoneBundleSupportGrace.Update();
        AutoArchiveCommands.RegisterRpcs();
        CreatorNameLookupRpc.Register();
        AutoArchiveService.Update();
        ZoneBoundaryOverlay.Update();
        AdminTerrainTool.Update();
    }

    public static void Shutdown()
    {
        ZonePieceCounter.Clear();
        ZoneBundleSupportGrace.Shutdown();
        ZoneBoundaryOverlay.Shutdown();
        AutoArchiveService.Shutdown();
        CreatorNameLookupRpc.ClearRateLimits();
        AutoArchiveStore.Flush(force: true);
        AdminTerrainTool.Shutdown();
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


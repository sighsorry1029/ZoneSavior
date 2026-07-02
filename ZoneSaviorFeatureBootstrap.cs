using BepInEx.Logging;
using HarmonyLib;

namespace ZoneSavior;

internal static class ZoneSaviorFeatureBootstrap
{
    public static void Initialize(ManualLogSource logger)
    {
        ZoneSaviorInputBlockers.Initialize(logger);
        ZoneLimitConfiguration.Initialize(ZoneSaviorPlugin.ConfigSync, logger);
        ZonePieceCounter.Initialize(logger);
        ZoneBundleCommands.Initialize(logger);
        ZoneBundleSupportGrace.Initialize(logger);
        AutoArchiveStore.Initialize(logger);
        AutoArchiveService.Initialize(logger);
        AutoArchiveCommands.Initialize(logger);
        AdminTerrainTool.Initialize(logger);
    }

    public static void InitializeCompat(ManualLogSource logger, Harmony harmony)
    {
        ZoneWorldEditTerrainCompat.Initialize(logger, harmony);
        ZoneSaviorExpandWorldDataCompat.Initialize(logger, harmony);
        AdminTerrainTool.InitializeCompat(harmony);
        VeiledRecipesCompat.Initialize(logger);
    }

    public static void Update()
    {
        ZoneBundleCommands.RegisterRpcs();
        ZoneBundleSupportGrace.Update();
        AutoArchiveCommands.RegisterRpcs();
        ZoneWorldEditTerrainCompat.Update();
        AutoArchiveService.Update();
        ZoneBoundaryOverlay.Update();
        AdminTerrainTool.Update();
    }

    public static void LateUpdate()
    {
        AdminTerrainTool.LateUpdate();
    }

    public static void Shutdown()
    {
        ZonePieceCounter.Clear();
        ZoneBundleSupportGrace.Shutdown();
        ZoneBoundaryOverlay.Shutdown();
        AutoArchiveService.Shutdown();
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


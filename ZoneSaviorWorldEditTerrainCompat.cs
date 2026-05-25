using System;
using System.Collections;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneWorldEditTerrainCompat
{
    private const string WorldEditCommandsGuid = "world_edit_commands";
    private static ManualLogSource? _logger;
    private static Harmony? _harmony;
    private static bool _patched;
    private static bool _missingLogged;

    internal static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        _logger = logger;
        _harmony = harmony;
        TryPatch();
    }

    internal static void Update()
    {
        if (!_patched)
        {
            TryPatch();
        }
    }

    private static void TryPatch()
    {
        if (_patched)
        {
            return;
        }

        Type? terrainType = GetWorldEditCommandsType("WorldEditCommands.Terrain");
        MethodInfo? resetMethod = terrainType?.GetMethod("ResetTerrain", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (resetMethod == null)
        {
            if (!_missingLogged)
            {
                _logger?.LogInfo("WorldEditCommands terrain reset compat: WorldEditCommands.Terrain.ResetTerrain not found yet.");
                _missingLogged = true;
            }

            return;
        }

        MethodInfo postfix = AccessTools.Method(typeof(ZoneWorldEditTerrainCompat), nameof(ResetTerrainPostfix));
        _harmony?.Patch(resetMethod, postfix: new HarmonyMethod(postfix));
        _patched = true;
        _logger?.LogInfo("WorldEditCommands terrain reset compat: patched terrain reset for ZoneSavior support-fill base layers.");
    }

    private static Type? GetWorldEditCommandsType(string fullName)
    {
        if (!Chainloader.PluginInfos.TryGetValue(WorldEditCommandsGuid, out var pluginInfo))
        {
            return null;
        }

        Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
        return assembly?.GetType(fullName, throwOnError: false);
    }

    private static void ResetTerrainPostfix(object __0, object __1, Vector3 __2, float __3)
    {
        try
        {
            ZoneBundleTerrain.ResetSupportFillBaseLayer(__0 as IEnumerable, __1 as IEnumerable, __2, __3);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"WorldEditCommands terrain reset compat failed: {ex}");
        }
    }
}

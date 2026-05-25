using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static class AdminTerrainInfinityHammerCompat
{
    private static ManualLogSource? _logger;
    private static bool _available;
    private static bool _patched;
    private static bool _saveDataConfigured;

    public static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        _logger = logger;
        if (_patched)
        {
            return;
        }

        _available = AccessTools.TypeByName("InfinityHammer.Configuration") != null;
        if (!_available)
        {
            _logger.LogDebug("Infinity Hammer compat skipped: Infinity Hammer is not loaded.");
            return;
        }

        int patched = 0;
        patched += PatchMethod(
            harmony,
            "InfinityHammer.BaseSelection:PostProcessPlaced",
            [typeof(GameObject)],
            nameof(InfinityHammerPostProcessPrefix),
            prefix: true);
        patched += PatchMethod(
            harmony,
            "InfinityHammer.BaseSelection:PostProcessPlaced",
            [typeof(GameObject)],
            nameof(InfinityHammerPostProcessPostfix),
            prefix: false);
        patched += PatchMethod(
            harmony,
            "InfinityHammer.NoCreator:Set",
            [typeof(ZNetView), typeof(Piece)],
            nameof(InfinityHammerNoCreatorPrefix),
            prefix: true);

        _patched = patched > 0;
        EnsureSavesTerrainData();
        _logger.LogInfo($"Infinity Hammer compat initialized. Patched {patched} method(s).");
    }

    public static void Update()
    {
        if (_available)
        {
            EnsureSavesTerrainData();
        }
    }

    public static void Shutdown()
    {
        _patched = false;
        _saveDataConfigured = false;
    }

    private static int PatchMethod(
        Harmony harmony,
        string fullMethodName,
        Type[] parameters,
        string patchMethodName,
        bool prefix)
    {
        MethodInfo? target = AccessTools.Method(fullMethodName, parameters);
        MethodInfo? patch = AccessTools.Method(typeof(AdminTerrainInfinityHammerCompat), patchMethodName);
        if (target == null || patch == null)
        {
            _logger?.LogDebug($"Infinity Hammer compat skipped method: {fullMethodName}");
            return 0;
        }

        HarmonyMethod harmonyMethod = new(patch);
        if (prefix)
        {
            harmony.Patch(target, prefix: harmonyMethod);
        }
        else
        {
            harmony.Patch(target, postfix: harmonyMethod);
        }

        return 1;
    }

    private static void InfinityHammerPostProcessPrefix(GameObject obj)
    {
        AdminTerrainTool.PrepareInfinityHammerPlaced(obj);
    }

    private static void InfinityHammerPostProcessPostfix(GameObject obj)
    {
        AdminTerrainTool.ApplyInfinityHammerPlaced(obj);
    }

    private static bool InfinityHammerNoCreatorPrefix(ZNetView view, Piece piece)
    {
        return AdminTerrainTool.ShouldRunInfinityHammerNoCreator(view, piece);
    }

    private static void EnsureSavesTerrainData()
    {
        if (_saveDataConfigured)
        {
            return;
        }

        Type type = AccessTools.TypeByName("InfinityHammer.Configuration");
        FieldInfo field = type?.GetField("SavedObjectData", BindingFlags.Public | BindingFlags.Static)!;
        if (field?.GetValue(null) is not HashSet<string> savedObjectData)
        {
            return;
        }

        savedObjectData.Add(AdminTerrainTool.PrefabName.ToLowerInvariant());
        savedObjectData.Add(AdminTerrainTool.SlopePrefabName.ToLowerInvariant());
        savedObjectData.Add(AdminTerrainTool.PaintPrefabName.ToLowerInvariant());
        _saveDataConfigured = true;
    }
}

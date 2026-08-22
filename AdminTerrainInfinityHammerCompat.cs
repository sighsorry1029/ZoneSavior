using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static class AdminTerrainInfinityHammerCompat
{
    private static readonly List<GameObject> DirectMenuSelectedPrefabs = [];

    private static ManualLogSource? _logger;
    private static bool _available;
    private static bool _patched;
    private static bool _saveDataConfigured;
    private static Type? _configurationType;
    private static MethodInfo? _getSelectedPieceMethod;

    public static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        _logger = logger;
        if (_patched)
        {
            return;
        }

        _configurationType = FindLoadedType("InfinityHammer.Configuration");
        _available = _configurationType != null;
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
        patched += PatchReplayBatchBoundary(harmony);
        patched += PatchDirectMenuSelectionConstructor(harmony);

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
        DirectMenuSelectedPrefabs.Clear();
        _available = false;
        _patched = false;
        _saveDataConfigured = false;
        _configurationType = null;
        _getSelectedPieceMethod = null;
    }

    internal static bool IsDirectMenuSelectionPrefab(GameObject selectedPrefab)
    {
        if (!_available || !selectedPrefab)
        {
            return false;
        }

        bool found = false;
        for (int i = DirectMenuSelectedPrefabs.Count - 1; i >= 0; i--)
        {
            GameObject candidate = DirectMenuSelectedPrefabs[i];
            if (!candidate)
            {
                DirectMenuSelectedPrefabs.RemoveAt(i);
            }
            else if (ReferenceEquals(candidate, selectedPrefab) ||
                     candidate.transform.IsChildOf(selectedPrefab.transform))
            {
                found = true;
            }
        }

        return found;
    }

    internal static bool IsCurrentDirectMenuSelection()
    {
        Player player = Player.m_localPlayer;
        GameObject selectedPrefab = player && player.m_buildPieces
            ? player.m_buildPieces.GetSelectedPrefab()
            : null!;
        return selectedPrefab && IsDirectMenuSelectionPrefab(selectedPrefab);
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var pluginInfo in Chainloader.PluginInfos.Values)
        {
            Type? type = pluginInfo.Instance?.GetType().Assembly.GetType(fullName, throwOnError: false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
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

    private static int PatchDirectMenuSelectionConstructor(Harmony harmony)
    {
        Type? objectSelectionType = FindLoadedType("InfinityHammer.ObjectSelection");
        ConstructorInfo? target = objectSelectionType == null
            ? null
            : AccessTools.Constructor(objectSelectionType, [typeof(Piece), typeof(bool)]);
        MethodInfo? patch = AccessTools.Method(
            typeof(AdminTerrainInfinityHammerCompat),
            nameof(InfinityHammerDirectMenuSelectionConstructed));
        _getSelectedPieceMethod = objectSelectionType?.GetMethod(
            "GetSelectedPiece",
            BindingFlags.Public | BindingFlags.Instance);
        if (target == null || patch == null || _getSelectedPieceMethod == null)
        {
            _logger?.LogDebug("Infinity Hammer compat skipped direct menu selection constructor.");
            _getSelectedPieceMethod = null;
            return 0;
        }

        harmony.Patch(target, postfix: new HarmonyMethod(patch));
        return 1;
    }

    private static int PatchReplayBatchBoundary(Harmony harmony)
    {
        MethodInfo? target = AccessTools.Method(
            typeof(Player),
            "PlacePiece",
            [typeof(Piece), typeof(Vector3), typeof(Quaternion), typeof(bool)]);
        MethodInfo? prefix = AccessTools.Method(
            typeof(AdminTerrainInfinityHammerCompat),
            nameof(InfinityHammerPlacePiecePrefix));
        MethodInfo? finalizer = AccessTools.Method(
            typeof(AdminTerrainInfinityHammerCompat),
            nameof(InfinityHammerPlacePieceFinalizer));
        if (target == null || prefix == null || finalizer == null)
        {
            _logger?.LogDebug("Infinity Hammer compat skipped Player.PlacePiece replay boundary.");
            return 0;
        }

        harmony.Patch(
            target,
            prefix: new HarmonyMethod(prefix),
            finalizer: new HarmonyMethod(finalizer));
        return 1;
    }

    private static void InfinityHammerDirectMenuSelectionConstructed(object __instance, bool singleUse)
    {
        try
        {
            if (singleUse ||
                _getSelectedPieceMethod?.Invoke(__instance, null) is not Piece piece ||
                !piece ||
                !AdminTerrainTool.IsProxyObject(piece.gameObject))
            {
                return;
            }

            GameObject selectedPrefab = piece.gameObject;
            DirectMenuSelectedPrefabs.RemoveAll(candidate =>
                !candidate || ReferenceEquals(candidate, selectedPrefab));
            DirectMenuSelectedPrefabs.Add(selectedPrefab);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug($"Infinity Hammer direct menu selection marker failed: {ex.Message}");
        }
    }

    private static void InfinityHammerPlacePiecePrefix(out bool __state)
    {
        __state = false;
        AdminTerrainTool.BeginBlueprintReplayBatch();
        __state = true;
    }

    private static Exception? InfinityHammerPlacePieceFinalizer(Exception? __exception, bool __state)
    {
        if (__state)
        {
            AdminTerrainTool.CompleteBlueprintReplayBatch(__exception == null);
        }

        return __exception;
    }

    private static void InfinityHammerPostProcessPrefix(GameObject obj)
    {
        AdminTerrainTool.PrepareInfinityHammerPlaced(obj);
    }

    private static void InfinityHammerPostProcessPostfix(GameObject obj)
    {
        AdminTerrainTool.ApplyInfinityHammerPlaced(obj, IsCurrentDirectMenuSelection());
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

        FieldInfo field = _configurationType?.GetField("SavedObjectData", BindingFlags.Public | BindingFlags.Static)!;
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

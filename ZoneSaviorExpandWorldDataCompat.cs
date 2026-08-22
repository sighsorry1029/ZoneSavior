using System;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneSaviorExpandWorldDataCompat
{
    private const string DataManagerTypeName = "ExpandWorldData.DataManager";
    private const string SpawnTypeName = "ExpandWorldData.Spawn";
    private const string BlueprintTypeName = "ExpandWorldData.Blueprint";

    private static ManualLogSource? _logger;
    private static bool _patched;
    private static bool _missingZdoLogged;

    public static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        _logger = logger;
        if (_patched)
        {
            return;
        }

        Type? dataManagerType = FindLoadedType(DataManagerTypeName);
        if (dataManagerType == null)
        {
            _logger.LogDebug("Expand World Data compat skipped: Expand World Data is not loaded.");
            return;
        }

        int patched = 0;
        patched += PatchCleanGhostInit(harmony, dataManagerType, typeof(GameObject), nameof(CleanGhostInitGameObjectPrefix));
        patched += PatchCleanGhostInit(harmony, dataManagerType, typeof(ZNetView), nameof(CleanGhostInitZNetViewPrefix));
        patched += PatchBlueprintReplayBoundary(harmony);

        _patched = patched > 0;
        _logger.LogInfo($"Expand World Data compat initialized. Patched {patched} method(s).");
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

    private static int PatchCleanGhostInit(Harmony harmony, Type dataManagerType, Type parameterType, string patchMethodName)
    {
        MethodInfo? target = AccessTools.Method(dataManagerType, "CleanGhostInit", new[] { parameterType });
        MethodInfo? patch = AccessTools.Method(typeof(ZoneSaviorExpandWorldDataCompat), patchMethodName);
        if (target == null || patch == null)
        {
            _logger?.LogDebug($"Expand World Data compat skipped CleanGhostInit({parameterType.Name}).");
            return 0;
        }

        harmony.Patch(target, prefix: new HarmonyMethod(patch));
        return 1;
    }

    private static int PatchBlueprintReplayBoundary(Harmony harmony)
    {
        Type? spawnType = FindLoadedType(SpawnTypeName);
        MethodInfo? target = spawnType?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, "Blueprint", StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 8 &&
                       string.Equals(parameters[0].ParameterType.FullName, BlueprintTypeName, StringComparison.Ordinal) &&
                       parameters[1].ParameterType == typeof(Vector3) &&
                       parameters[2].ParameterType == typeof(Quaternion) &&
                       parameters[3].ParameterType == typeof(Vector3) &&
                       parameters[4].ParameterType == typeof(int);
            });
        MethodInfo? prefix = AccessTools.Method(
            typeof(ZoneSaviorExpandWorldDataCompat),
            nameof(BlueprintReplayPrefix));
        MethodInfo? finalizer = AccessTools.Method(
            typeof(ZoneSaviorExpandWorldDataCompat),
            nameof(BlueprintReplayFinalizer));
        if (target == null || prefix == null || finalizer == null)
        {
            _logger?.LogDebug("Expand World Data compat skipped Spawn.Blueprint replay boundary.");
            return 0;
        }

        harmony.Patch(
            target,
            prefix: new HarmonyMethod(prefix),
            finalizer: new HarmonyMethod(finalizer));
        return 1;
    }

    private static void BlueprintReplayPrefix(out bool __state)
    {
        __state = false;
        AdminTerrainTool.BeginBlueprintReplayBatch();
        __state = true;
    }

    private static Exception? BlueprintReplayFinalizer(Exception? __exception, bool __state)
    {
        if (__state)
        {
            AdminTerrainTool.CompleteBlueprintReplayBatch(__exception == null);
        }

        return __exception;
    }

    private static bool CleanGhostInitGameObjectPrefix(GameObject obj)
    {
        if (!ShouldHandle(obj))
        {
            return true;
        }

        InitializeProxy(obj, obj.GetComponent<ZNetView>());
        return false;
    }

    private static bool CleanGhostInitZNetViewPrefix(ZNetView view)
    {
        GameObject obj = view ? view.gameObject : null!;
        if (!ShouldHandle(obj))
        {
            return true;
        }

        InitializeProxy(obj, view);
        return false;
    }

    private static bool ShouldHandle(GameObject obj)
    {
        return obj && AdminTerrainTool.IsProxyObject(obj);
    }

    private static void InitializeProxy(GameObject obj, ZNetView? view)
    {
        bool ghostInit = ZNetView.m_ghostInit;
        ZDO? pendingInitZdo = GetMatchingPendingInitZdo(obj);
        if (!obj.activeSelf)
        {
            obj.SetActive(true);
        }

        view = view ? view : obj.GetComponent<ZNetView>();
        if (!view)
        {
            AdminTerrainTool.FailBlueprintReplayBatch();
            DiscardFailedProxyInitialization(obj, pendingInitZdo);
            return;
        }

        ZDO? zdo = view.GetZDO();
        if (zdo == null && ghostInit)
        {
            zdo = TryConsumePendingInitZdo(view, pendingInitZdo);
        }

        if (zdo == null)
        {
            AdminTerrainTool.FailBlueprintReplayBatch();
            DiscardFailedProxyInitialization(obj, pendingInitZdo);
            return;
        }

        ZoneSaviorTerrainProxy proxy = obj.GetComponent<ZoneSaviorTerrainProxy>();
        bool replayDeferred = proxy &&
                              AdminTerrainTool.FinalizeBlueprintReplayProxyRegistration(
                                  proxy,
                                  zdo,
                                  liveProxy: !ghostInit);

        if (!ghostInit)
        {
            if (proxy && !replayDeferred)
            {
                AdminTerrainTool.ApplyLoadedProxy(proxy);
            }

            return;
        }

        view.m_ghost = true;
        zdo.Created = false;
        ZNetScene.instance?.m_instances.Remove(zdo);
    }

    private static ZDO? GetMatchingPendingInitZdo(GameObject obj)
    {
        ZDO? pending = ZNetView.m_initZDO;
        if (pending == null || pending.GetPrefab() != StringExtensionMethods.GetStableHashCode(Utils.GetPrefabName(obj)))
        {
            return null;
        }

        return pending;
    }

    private static ZDO? TryConsumePendingInitZdo(ZNetView view, ZDO? pending)
    {
        if (pending == null || !ReferenceEquals(ZNetView.m_initZDO, pending))
        {
            return null;
        }

        view.m_zdo = pending;
        ZNetView.m_initZDO = null;
        return pending;
    }

    private static void DiscardFailedProxyInitialization(GameObject obj, ZDO? pending)
    {
        if (pending != null)
        {
            if (ReferenceEquals(ZNetView.m_initZDO, pending))
            {
                ZNetView.m_initZDO = null;
            }

            pending.Created = false;
            ZNetScene.instance?.m_instances.Remove(pending);
        }

        LogMissingZdoOnce(obj);
        UnityEngine.Object.Destroy(obj);
    }

    private static void LogMissingZdoOnce(GameObject obj)
    {
        if (_missingZdoLogged)
        {
            return;
        }

        _missingZdoLogged = true;
        _logger?.LogWarning($"Expand World Data compat could not initialize ZoneSavior proxy ZDO for {Utils.GetPrefabName(obj)}.");
    }
}

using System;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneSaviorTerrainMistileCompat
{
    private const string CompatTypeName = "TerrainMistile.TerrainMistileCompat";
    private static MethodInfo? _registerIgnoredTerrainAreaMethod;
    private static bool _lookupCompleted;

    public static void RegisterIgnoredTerrainArea(Vector3 center, float radius, string source)
    {
        if (!_lookupCompleted)
        {
            _registerIgnoredTerrainAreaMethod = FindRegisterIgnoredTerrainAreaMethod();
            _lookupCompleted = true;
        }

        if (_registerIgnoredTerrainAreaMethod == null)
        {
            return;
        }

        try
        {
            _registerIgnoredTerrainAreaMethod.Invoke(null, new object[] { center, radius, source });
        }
        catch (Exception ex)
        {
            _registerIgnoredTerrainAreaMethod = null;
            _lookupCompleted = false;
            ZoneSaviorPlugin.ZoneSaviorLogger.LogDebug($"TerrainMistile compat call failed: {ex.Message}");
        }
    }

    private static MethodInfo? FindRegisterIgnoredTerrainAreaMethod()
    {
        Type? compatType = FindLoadedType(CompatTypeName);
        return compatType?.GetMethod(
            "RegisterIgnoredTerrainArea",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Vector3), typeof(float), typeof(string) },
            null);
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
}

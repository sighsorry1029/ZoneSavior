using System;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace ZoneSavior;

internal static class VeiledRecipesCompat
{
    private const string PluginGuid = "sighsorry.VeiledRecipes";
    private const string ApiTypeName = "VeiledRecipes.VeiledRecipesCompat";
    private static readonly BindingFlags PublicStaticFlags = BindingFlags.Public | BindingFlags.Static;

    private static ManualLogSource? _logger;
    private static bool _initialized;
    private static bool _registered;
    private static MethodInfo? _registerKnownPieceOverrideMethod;
    private static readonly Func<Piece, bool> KnownPieceOverride = AdminTerrainTool.IsProxyPiece;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        EnsureInitialized();
        RegisterKnownPieceOverride();
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var pluginInfo))
        {
            return;
        }

        Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
        Type? apiType = assembly?.GetType(ApiTypeName, throwOnError: false);
        _registerKnownPieceOverrideMethod = apiType?.GetMethod(
            "RegisterKnownPieceOverride",
            PublicStaticFlags,
            null,
            new[] { typeof(Func<Piece, bool>) },
            null);
    }

    private static void RegisterKnownPieceOverride()
    {
        if (_registered || _registerKnownPieceOverrideMethod == null)
        {
            return;
        }

        try
        {
            _registerKnownPieceOverrideMethod.Invoke(null, new object[] { KnownPieceOverride });
            _registered = true;
            _logger?.LogDebug("Registered ZoneSavior admin terrain tools with VeiledRecipes.");
        }
        catch (Exception ex)
        {
            _logger?.LogDebug($"Could not register ZoneSavior admin terrain tools with VeiledRecipes: {ex.Message}");
        }
    }
}

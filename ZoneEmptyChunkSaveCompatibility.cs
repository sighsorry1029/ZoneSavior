using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace ZoneSavior;

/// <summary>Only one recognized empty-chunk strategy participates. Never veto a world save.</summary>
internal static class ZoneEmptyChunkSaveCompatibility
{
    internal enum Strategy { ZoneSavior, UpgradeWorld, Unsupported }

    private const string UpgradeOwner = "upgrade_world";
    private static (MethodInfo Target, string Type, string Method, bool Prefix)[]? targets;
    private static (MethodInfo Target, string Type, string Method, bool Prefix)[] Targets => targets ??= new[]
    {
        (Required(typeof(ZDOMan), "AddObjectsPerChunk", typeof(int), typeof(byte), typeof(int[]),
            typeof(List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>>).MakeByRefType()), "UpgradeWorld.FindEmptiedChunks", "Prefix", true),
        (Required(typeof(ChunkSaveMapping), nameof(ChunkSaveMapping.Clone)), "UpgradeWorld.DropEmptiedChunks", "Postfix", false),
        (Required(typeof(ZDOMan), nameof(ZDOMan.DeleteOldChunks), typeof(string), typeof(FileHelpers.FileSource)),
            "UpgradeWorld.DeleteEmptiedChunkFiles", "Prefix", true)
    };
    private static string? lastReport;
    private static ManualLogSource? logger;
    internal static string? DisabledReason { get; private set; }

    // The plugin supplies its existing logger after initialization. Isolated checks can omit it
    // without initializing ZoneSaviorPlugin's configuration paths or Unity-dependent state.
    internal static void Reset(ManualLogSource? log = null)
    {
        DisabledReason = null;
        lastReport = null;
        logger = log;
    }

    internal static void RequireSafeMutation()
    {
        if (Check() == Strategy.Unsupported)
            throw new InvalidOperationException(DisabledReason);
    }

    private static MethodInfo Required(Type type, string name, params Type[] arguments) =>
        AccessTools.DeclaredMethod(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);

    // Cache only native method identities. Plugins can patch after ZoneSavior.Awake or unpatch later.
    internal static Strategy Inspect(out string detail)
    {
        var found = new List<string>();
        var valid = 0;
        Assembly? implementation = null;
        var sameImplementation = true;
        foreach (var expected in Targets)
        {
            var info = Harmony.GetPatchInfo(expected.Target);
            if (info == null) continue;
            var relevant = 0;
            var matches = 0;
            foreach (var pair in new[] { ("Prefix", info.Prefixes), ("Postfix", info.Postfixes),
                         ("Transpiler", info.Transpilers), ("Finalizer", info.Finalizers) })
            foreach (var patch in pair.Item2)
            {
                var method = patch.PatchMethod;
                var assembly = method.DeclaringType?.Assembly;
                if (patch.owner != UpgradeOwner && assembly?.GetName().Name != "UpgradeWorld" &&
                    method.DeclaringType?.Namespace != "UpgradeWorld") continue;
                relevant++;
                found.Add($"{expected.Target.DeclaringType!.Name}.{expected.Target.Name}: {pair.Item1} {patch.owner}/" +
                    $"{method.DeclaringType?.FullName}.{method.Name} [priority={patch.priority}; before={string.Join(",", patch.before)}; " +
                    $"after={string.Join(",", patch.after)}; {assembly?.FullName}; MVID={method.Module.ModuleVersionId}]");
                if (patch.owner == UpgradeOwner && method.DeclaringType?.FullName == expected.Type &&
                    method.Name == expected.Method && method.IsStatic && method.ReturnType == typeof(void) &&
                    pair.Item1 == (expected.Prefix ? "Prefix" : "Postfix") &&
                    assembly?.GetName().Name == "UpgradeWorld")
                {
                    matches++;
                    if (implementation == null) implementation = assembly;
                    else if (!ReferenceEquals(implementation, assembly)) sameImplementation = false;
                }
            }
            if (relevant == 1 && matches == 1) valid++;
        }
        detail = string.Join("; ", found);
        if (found.Count == 0) return Strategy.ZoneSavior;
        if (found.Count != Targets.Length || valid != Targets.Length || !sameImplementation || implementation == null)
            return Strategy.Unsupported;
        var recognized = ZoneUpgradeWorldSaveContract.Recognizes(implementation, out var contract);
        detail += "; " + contract;
        return recognized ? Strategy.UpgradeWorld : Strategy.Unsupported;
    }

    internal static Strategy Check()
    {
        // Restart is required after a failure; do not silently resume destructive work.
        if (DisabledReason != null) return Strategy.Unsupported;
        Strategy strategy;
        string detail;
        try
        {
            strategy = Inspect(out detail);
            if (strategy == Strategy.ZoneSavior) ZoneEmptyChunkSavePatch.ValidateContract();
        }
        catch (Exception error) { strategy = Strategy.Unsupported; detail = error.GetType().Name + ": " + error.Message; }
        if (strategy == Strategy.Unsupported)
            DisabledReason = "Empty-chunk save compatibility could not be verified. " + detail;
        var report = strategy + ":" + detail;
        if (report != lastReport)
        {
            lastReport = report;
            var message = strategy == Strategy.UpgradeWorld
                ? "ZoneSavior empty chunk compatibility: UpgradeWorld cleanup contract recognized; ZoneSavior empty replacements are disabled."
                : strategy == Strategy.ZoneSavior
                    ? "ZoneSavior empty chunk compatibility: ZoneSavior strategy active."
                    : "ZoneSavior reset/target replacement disabled: " + DisabledReason +
                      " Destructive world changes and ZoneSavior empty replacements are disabled. " +
                      "World saves have NOT been blocked by ZoneSavior. External save behavior is not verified; " +
                      "resolve the combination and restart to re-enable these operations.";
            if (strategy == Strategy.Unsupported) logger?.LogError(message);
            else logger?.LogInfo(message);
        }
        return strategy;
    }
}

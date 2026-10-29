// Isolated managed execution only. All file fixtures live beneath a unique Temp folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace ZoneSavior.Tests;

internal static class ProbeContext
{
    internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static Assembly Plugin = null!, Upgrade = null!, Fresh = null!;
    internal static Type ZonePatch = null!, Compatibility = null!, Contract = null!, FreshCompatibility = null!;
    internal static readonly Harmony ZoneHarmony = new("sighsorry.ZoneSavior");
    internal static readonly Harmony FreshHarmony = new("sighsorry.FreshWorld");
    internal static readonly Harmony UpgradeHarmony = new("upgrade_world");
    internal static readonly Harmony BoundaryHarmony = new("ZoneSavior.EmptyChunkTests.Boundary");
    internal static string UpgradePath = "";
    internal static int Checks;

    internal static MethodInfo Method(Type type, string name) => type.GetMethod(name, All)!;
    internal static object? Call(Type type, object? instance, string name, params object?[] args) => Method(type, name).Invoke(instance, args);
    internal static void Set(object instance, string name, object value) => instance.GetType().GetField(name, All)!.SetValue(instance, value);
    internal static object Get(object instance, string name) => instance.GetType().GetField(name, All)!.GetValue(instance)!;
    internal static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Equal(object expected, object? actual) => Require(expected.Equals(actual), $"Expected {expected}; got {actual}.");
    internal static string Check() => Call(Compatibility, null, "Check")!.ToString()!;
    internal static void Reset()
    {
        Call(Compatibility, null, "Reset", new object?[] { null });
        Call(FreshCompatibility, null, "Reset");
    }
    internal static void UnpatchStrategies()
    {
        ZoneHarmony.UnpatchSelf(); FreshHarmony.UnpatchSelf(); UpgradeHarmony.UnpatchSelf();
    }
    internal static void InstallZone() => ZoneHarmony.CreateClassProcessor(ZonePatch).Patch();
    internal static void InstallFresh() => FreshHarmony.CreateClassProcessor(Fresh.GetType("FreshWorld.Engine.EmptyChunkSavePatch", true)!).Patch();
    internal static void InstallUpgrade(int mask = 7)
    {
        UpgradeHarmony.UnpatchSelf();
        var names = new[] { "FindEmptiedChunks", "DropEmptiedChunks", "DeleteEmptiedChunkFiles" };
        for (var i = 0; i < names.Length; i++)
            if ((mask & (1 << i)) != 0) UpgradeHarmony.CreateClassProcessor(Upgrade.GetType("UpgradeWorld." + names[i], true)!).Patch();
    }
    internal static void Test(string name, Action action)
    {
        UnpatchStrategies(); Reset();
        action(); Checks++; System.Console.WriteLine("PASS " + name);
    }
    internal static string Sha(string path)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
    }
}

// Entry-point name deliberately matches the existing CompatibilityMonoHost.
public static class CompatibilityProbe
{
    public static int Run()
    {
        try
        {
            var managed = Environment.GetEnvironmentVariable("ZONESAVIOR_MONO_PROBE_MANAGED")!;
            var modPath = Environment.GetEnvironmentVariable("ZONESAVIOR_MONO_PROBE_DLL")!;
            ProbeContext.UpgradePath = Environment.GetEnvironmentVariable("ZONESAVIOR_EMPTY_UPGRADE_DLL")!;
            var freshPath = Environment.GetEnvironmentVariable("ZONESAVIOR_EMPTY_FRESH_DLL")!;
            AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
            {
                foreach (var directory in new[] { managed, Path.GetDirectoryName(modPath)!, Path.GetDirectoryName(freshPath)! })
                {
                    var path = Path.Combine(directory, new AssemblyName(request.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            ProbeContext.Require(string.Equals(Path.GetFullPath(typeof(ZDO).Assembly.Location),
                Path.GetFullPath(Path.Combine(managed, "assembly_valheim.dll")), StringComparison.OrdinalIgnoreCase), "Original game DLL was not selected.");
            ProbeContext.Plugin = Assembly.LoadFrom(modPath);
            ProbeContext.Upgrade = Assembly.LoadFrom(ProbeContext.UpgradePath);
            ProbeContext.Fresh = Assembly.LoadFrom(freshPath);
            ProbeContext.ZonePatch = ProbeContext.Plugin.GetType("ZoneSavior.ZoneEmptyChunkSavePatch", true)!;
            ProbeContext.Compatibility = ProbeContext.Plugin.GetType("ZoneSavior.ZoneEmptyChunkSaveCompatibility", true)!;
            ProbeContext.Contract = ProbeContext.Plugin.GetType("ZoneSavior.ZoneUpgradeWorldSaveContract", true)!;
            ProbeContext.FreshCompatibility = ProbeContext.Fresh.GetType("FreshWorld.Engine.EmptyChunkSaveCompatibility", true)!;
            foreach (var path in new[] { typeof(ZDO).Assembly.Location, modPath, ProbeContext.UpgradePath, freshPath })
                System.Console.WriteLine("INPUT " + path + " SHA256=" + ProbeContext.Sha(path));

            // Suppress only diagnostics and the platform-backed cloud checks. Save selection,
            // mapping, file I/O, Harmony patches and compatibility decisions remain real.
            ProbeContext.BoundaryHarmony.Patch(typeof(UnityEngine.Application).GetProperty("persistentDataPath", ProbeContext.All)!.GetMethod!,
                prefix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(TemporaryDataPath)));
            foreach (var property in new[] { "CloudStorageSupported", "CloudStorageSupportedAndEnabled" })
                ProbeContext.BoundaryHarmony.Patch(typeof(FileHelpers).GetProperty(property, ProbeContext.All)!.GetMethod!,
                    prefix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(NoCloud)));
            foreach (var method in typeof(ZLog).GetMethods(ProbeContext.All).Where(m => m.Name == "Log" && m.ReturnType == typeof(void)))
                ProbeContext.BoundaryHarmony.Patch(method, prefix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(Skip)));
            ProbeContext.BoundaryHarmony.Patch(ProbeContext.Method(ProbeContext.Fresh.GetType("FreshWorld.FreshWorldPlugin", true)!, "LogSaveCompatibility"),
                prefix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(Skip)));
            ProbeContext.Reset();
            ProbeContext.Equal("ZoneSavior", ProbeContext.Check());
            if (EmptyChunkSelectionChecks.Run() != 0) return 1;
            EmptyChunkHarmonyChecks.Run();
            MutationGuardRegression.Run();
            System.Console.WriteLine("PASS 18 selection checks + " + ProbeContext.Checks + " real Harmony/compatibility/storage checks.");
            System.Console.WriteLine("Original DLLs / isolated Unity Mono. No plugin Awake, Unity scene, live world, socket, or cloud save executed.");
            return 0;
        }
        catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
        finally
        {
            foreach (var harmony in new[] { ProbeContext.ZoneHarmony, ProbeContext.FreshHarmony, ProbeContext.UpgradeHarmony, ProbeContext.BoundaryHarmony })
                try { harmony.UnpatchSelf(); } catch (Exception error) { System.Console.Error.WriteLine("Cleanup: " + error.Message); }
        }
    }
    private static bool NoCloud(ref bool __result) { __result = false; return false; }
    private static bool TemporaryDataPath(ref string __result) { __result = Path.GetTempPath(); return false; }
    private static bool Skip() => false;
}

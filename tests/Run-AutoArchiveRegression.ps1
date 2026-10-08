#requires -Version 5.1
param(
    [string] $AssemblyPath = (Join-Path $PSScriptRoot '../bin/ZoneSavior/Debug/ZoneSavior.dll'),
    [string] $DependencyDirectory = (Join-Path $PSScriptRoot '../bin/ZoneSavior/Debug')
)

# Managed policy and manifest tests only; no world, plugin, ZDO destruction or game configuration.
# Manifest round trips use a unique temporary directory.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this script with Windows PowerShell (powershell.exe).' }
$resolvedAssembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
$resolvedDependencies = (Resolve-Path -LiteralPath $DependencyDirectory).Path

Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class AutoArchiveRegression
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type store, serialization, scanner, zoneType, infoType, manifestType, entryType, modelZoneType;
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }
    private static object Get(object instance, string property) { return instance.GetType().GetProperty(property).GetValue(instance, null); }
    private static void Set(object instance, string property, object value) { instance.GetType().GetProperty(property).SetValue(instance, value, null); }
    private static object Call(Type type, string name, params object[] args) { return type.GetMethod(name, Static).Invoke(null, args); }
    private static void Rejected(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException error)
        {
            Exception cause = error;
            while (cause is TargetInvocationException && cause.InnerException != null) cause = cause.InnerException;
            Check(cause is InvalidDataException || cause is InvalidOperationException, message + ": expected validation failure, got " + cause);
            return;
        }
        throw new Exception(message + ": operation was accepted");
    }
    private static object Zone(int x, int z) { return Activator.CreateInstance(zoneType, new object[] { x, z }); }
    private static object ModelZone(int x, int z)
    {
        object result = Activator.CreateInstance(modelZoneType, true);
        Set(result, "X", x); Set(result, "Z", z);
        return result;
    }
    private static IList ZoneList(bool models, params int[] coordinates)
    {
        IList result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(models ? modelZoneType : zoneType));
        for (int i = 0; i < coordinates.Length; i += 2)
            result.Add(models ? ModelZone(coordinates[i], coordinates[i + 1]) : Zone(coordinates[i], coordinates[i + 1]));
        return result;
    }
    private static string ZoneKey(object zone)
    {
        if (zone.GetType() == zoneType)
            return zoneType.GetField("x").GetValue(zone) + "," + zoneType.GetField("y").GetValue(zone);
        return Get(zone, "X") + "," + Get(zone, "Z");
    }
    private static string ZoneKeys(IEnumerable zones)
    {
        List<string> keys = new List<string>();
        foreach (object zone in zones) keys.Add(ZoneKey(zone));
        keys.Sort(StringComparer.Ordinal);
        return String.Join(";", keys.ToArray());
    }
    private static IDictionary Infos(params long[][] rows)
    {
        IDictionary result = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(zoneType, infoType));
        foreach (long[] row in rows)
        {
            object zone = Zone((int)row[0], (int)row[1]);
            object info = Activator.CreateInstance(infoType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { zone }, null);
            for (int i = 2; i < row.Length; i++) infoType.GetMethod("AddCreator").Invoke(info, new object[] { row[i] });
            result.Add(zone, info);
        }
        return result;
    }
    private static string Plans(IDictionary infos, bool mixedOnly, params long[] eligible)
    {
        IEnumerable plans = (IEnumerable)Call(scanner, "BuildArchivePlans", infos, new HashSet<long>(eligible), mixedOnly);
        List<string> keys = new List<string>();
        foreach (object plan in plans)
            keys.Add(ZoneKeys((IEnumerable)Get(plan, "Zones")) + " / " + ZoneKeys((IEnumerable)Get(plan, "ResetZones")));
        keys.Sort(StringComparer.Ordinal);
        return String.Join(" | ", keys.ToArray());
    }
    private static void Planning()
    {
        // IDs 1=A and 2=B are inactive; ID 3=C is active.
        IDictionary drawing = Infos(new long[] { 0, 0, 1, 2 }, new long[] { 1, 0, 2, 3 }, new long[] { 2, 0, 3 });
        Check(Plans(drawing, false, 1, 2) == "0,0;1,0 / 0,0", "Drawing: back up A+B and B+C, reset A+B, exclude C-only zone");
        Check(Plans(drawing, false, 1) == "", "No pure reset core: a target cannot reset another creator's pieces");
        Check(Plans(drawing, true, 1) == "0,0 / ", "Explicit save-only targeting preserves mixed-only backup");
        Check(Plans(drawing, false) == "", "No eligible creators produces no automatic archive");

        Check(Plans(Infos(new long[] { 0, 0, 1 }, new long[] { 1, 1, 2 }), false, 1, 2) ==
            "0,0;1,1 / 0,0;1,1", "Adjacent pure zones retain eight-neighbor clustering even with different owners");
        Check(Plans(Infos(new long[] { 0, 0, 1 }, new long[] { 2, 0, 1 }), false, 1) ==
            "0,0 / 0,0 | 2,0 / 2,0", "Shared creator alone cannot connect nonadjacent zones");
        Check(Plans(Infos(new long[] { 0, 0, 1 }, new long[] { 1, 0, 1, 3 }, new long[] { 2, 0, 1, 3 }), false, 1) ==
            "0,0;1,0;2,0 / 0,0", "An inactive owner's contiguous construction is saved across multiple mixed zones");
        Check(Plans(Infos(new long[] { 0, 0, 1 }, new long[] { 1, 0, 1, 3 }, new long[] { 2, 0, 3 }, new long[] { 3, 0, 1, 3 }), false, 1) ==
            "0,0;1,0 / 0,0", "An active-only zone does not bridge unrelated distant structures");

        // D is also inactive, but the two mixed zones share only active C.
        Check(Plans(Infos(new long[] { 0, 0, 1, 2 }, new long[] { 1, 0, 2, 3 }, new long[] { 2, 0, 4, 3 }, new long[] { 3, 0, 4 }), false, 1, 2, 4) ==
            "0,0;1,0 / 0,0 | 2,0;3,0 / 3,0", "Active C cannot merge the A/B and D archives");
        Check(Plans(Infos(new long[] { 0, 0, 1 }, new long[] { 1, 0, 2, 3 }), false, 1, 2) ==
            "0,0 / 0,0", "A mixed neighbor requires an eligible creator shared with this core");
        Check(Plans(Infos(new long[] { 0, 0, 1 }, new long[] { 1, 0, 1, 0 }), false, 1) ==
            "0,0;1,0 / 0,0", "Unknown or creatorless content prevents reset but not an owner's contextual backup");
        IDictionary reversed = Infos(new long[] { 2, 0, 3 }, new long[] { 1, 0, 2, 3 }, new long[] { 0, 0, 1, 2 });
        Check(Plans(reversed, false, 2, 1) == Plans(drawing, false, 1, 2), "Archive membership is independent of scan order");
        System.Console.WriteLine("PASS auto archive planning: mixed-owner context, reset subset, disconnected/active boundaries and targeted save-only");
    }
    private static object Manifest(int version, bool resetAfterSave, params int[] completed)
    {
        object manifest = Activator.CreateInstance(manifestType, true);
        Set(manifest, "Version", version); Set(manifest, "Tag", "auto-regression"); Set(manifest, "World", "test-world");
        object range = Get(manifest, "SourceRange");
        Set(range, "MinX", 0); Set(range, "MaxX", 2); Set(range, "MinZ", 0); Set(range, "MaxZ", 0);
        IList bundles = (IList)Get(manifest, "Bundles");
        for (int x = 0; x < 3; x++)
        {
            object entry = Activator.CreateInstance(entryType, true);
            Set(entry, "Zone", ModelZone(x, 0)); Set(entry, "File", "bundle" + x + ".zonebundle.yml.gz");
            bundles.Add(entry);
        }
        if (version == 3)
        {
            Set(manifest, "WorldUid", 11L);
            Set(manifest, "ResetAfterSave", resetAfterSave);
            Set(manifest, "ResetEligibleZones", ZoneList(true, 0, 0, 1, 0));
            Set(manifest, "ResetCompletedZones", ZoneList(true, completed));
        }
        return manifest;
    }
    private static string RestoreKeys(object manifest)
    {
        List<object> zones = new List<object>();
        foreach (object entry in (IEnumerable)Call(store, "GetOriginalRestoreEntries", manifest)) zones.Add(Get(entry, "Zone"));
        return ZoneKeys(zones);
    }
    private static void RestoreSelection()
    {
        Check(RestoreKeys(Manifest(2, false)) == "0,0;1,0;2,0", "Legacy manual manifest restores every original zone");
        Check(RestoreKeys(Manifest(3, false)) == "0,0;1,0", "Save-only archive excludes retained mixed context from default restore");
        Rejected(delegate { RestoreKeys(Manifest(3, true)); }, "Reset archive cannot restore before any reset completed");
        Check(RestoreKeys(Manifest(3, true, 0, 0)) == "0,0", "Partial reset restores only confirmed completed zone");
        Check(RestoreKeys(Manifest(3, true, 0, 0, 1, 0)) == "0,0;1,0", "Completed core restores without overwriting retained context");
        object pending = Manifest(3, true, 0, 0);
        Set(pending, "ResetPendingZone", ModelZone(1, 0));
        Rejected(delegate { RestoreKeys(pending); }, "Uncertain partial destruction blocks default restore");

        object partial = Manifest(3, true, 0, 0);
        Call(store, "ValidateLoadTargets", partial, ZoneList(false, 0, 0, 10, 10), "test-world", 11L);
        Check(true, "Completed source and unrelated destination zones are permitted");
        Rejected(delegate { Call(store, "ValidateLoadTargets", partial, ZoneList(false, 0, 0, 1, 0), "test-world", 11L); },
            "An unreset eligible zone remains protected after partial reset");
        Rejected(delegate { Call(store, "ValidateLoadTargets", partial, ZoneList(false, 2, 0), "test-world", 11L); },
            "Direct source-zone load cannot overwrite retained mixed context");
        Rejected(delegate { Call(store, "ValidateLoadTargets", partial, ZoneList(false, 0, 0, 1, 0, 2, 0), "test-world", 11L); },
            "Whole archive original footprint cannot overwrite retained source zones");
        Call(store, "ValidateLoadTargets", partial, ZoneList(false, 1, 0, 2, 0), "other-world", 22L);
        Check(true, "Same coordinates in another world do not identify the retained source");
        Call(store, "ValidateLoadTargets", partial, ZoneList(false, 1, 0, 2, 0), "test-world", 22L);
        Check(true, "Two different world UIDs can share the same display name");
        Rejected(delegate { Call(store, "ValidateLoadTargets", partial, ZoneList(false, 2, 0), "renamed-world", 11L); },
            "Renaming the source world cannot bypass retained-source protection");
        Rejected(delegate { Call(store, "ValidateLoadTargets", partial, ZoneList(false, 2, 0), "other-world", null); },
            "Without a current world UID, a differing display name is not proof of a different world");
        Call(store, "ValidateLoadTargets", Manifest(3, false), ZoneList(false, 0, 0, 1, 0), "test-world", 11L);
        Check(true, "Save-only core can be explicitly restored at its source");
        Rejected(delegate { Call(store, "ValidateLoadTargets", Manifest(3, false), ZoneList(false, 2, 0), "test-world", 11L); },
            "Save-only context is also protected from same-world replacement");
        Rejected(delegate { Call(store, "ValidateLoadTargets", pending, ZoneList(false, 10, 10), "other-world", 22L); },
            "Pending reset blocks all loads until its ambiguous outcome is resolved");
        Call(store, "ValidateLoadTargets", Manifest(2, false), ZoneList(false, 0, 0, 1, 0, 2, 0), "test-world", 11L);
        Check(true, "Legacy manual source loading remains available");
        System.Console.WriteLine("PASS auto archive restore safety: legacy, save-only, partial reset, retained-source protection and pending journal");
    }
    private static void InvalidManifest(Action<object> mutation, string message)
    {
        object manifest = Manifest(3, true);
        mutation(manifest);
        Rejected(delegate { Call(serialization, "ValidateManifest", manifest); }, message);
    }
    private static void ManifestValidation()
    {
        InvalidManifest(delegate(object m) { Set(m, "ResetAfterSave", null); }, "v3 requires explicit reset intent");
        InvalidManifest(delegate(object m) { Set(m, "World", ""); }, "v3 requires source world identity");
        InvalidManifest(delegate(object m) { Set(m, "WorldUid", null); }, "v3 requires source world UID rather than only a mutable name");
        InvalidManifest(delegate(object m) { Set(m, "ResetEligibleZones", null); }, "v3 requires eligible list");
        InvalidManifest(delegate(object m) { Set(m, "ResetEligibleZones", ZoneList(true)); }, "v3 requires at least one reset-eligible zone");
        InvalidManifest(delegate(object m) { Set(m, "ResetCompletedZones", null); }, "Missing completion journal must not mean all zones completed");
        InvalidManifest(delegate(object m) { Set(m, "ResetEligibleZones", ZoneList(true, 3, 0)); }, "Eligible zone must have a saved bundle");
        InvalidManifest(delegate(object m) { Set(m, "ResetEligibleZones", ZoneList(true, 0, 0, 0, 0)); }, "Duplicate eligible zones are rejected");
        InvalidManifest(delegate(object m) { Set(m, "ResetCompletedZones", ZoneList(true, 2, 0)); }, "Retained context cannot be marked completed");
        InvalidManifest(delegate(object m) { Set(m, "ResetCompletedZones", ZoneList(true, 0, 0, 0, 0)); }, "Duplicate completed zones are rejected");
        InvalidManifest(delegate(object m) { Set(m, "ResetPendingZone", ModelZone(2, 0)); }, "Pending zone must be eligible");
        InvalidManifest(delegate(object m) { Set(m, "ResetPendingZone", ModelZone(0, 0)); Set(m, "ResetCompletedZones", ZoneList(true, 0, 0)); },
            "A zone cannot be pending and completed at once");
        InvalidManifest(delegate(object m) { Set(m, "ResetAfterSave", false); Set(m, "ResetCompletedZones", ZoneList(true, 0, 0)); },
            "Save-only archive cannot contain a completed reset");
        InvalidManifest(delegate(object m) { Set(m, "ResetAfterSave", false); Set(m, "ResetPendingZone", ModelZone(0, 0)); },
            "Save-only archive cannot contain a pending reset");
        object downgraded = Manifest(3, true, 0, 0);
        Set(downgraded, "Version", 2);
        Rejected(delegate { Call(serialization, "ValidateManifest", downgraded); }, "v3 safety metadata cannot masquerade as an unrestricted v2 manifest");

        string directory = Path.Combine(Path.GetTempPath(), "ZoneSavior-AutoArchive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (bool reset in new[] { false, true })
            {
                string path = Path.Combine(directory, reset ? "reset.yml" : "save-only.yml");
                Call(serialization, "SaveManifest", path, Manifest(3, reset));
                object loaded = Call(serialization, "LoadManifest", path);
                Check((int)Get(loaded, "Version") == 3 && (bool)Get(loaded, "ResetAfterSave") == reset, "Manifest round trip preserves version and reset intent");
                Check((long)Get(loaded, "WorldUid") == 11L, "Manifest round trip preserves immutable source world UID");
                Check(ZoneKeys((IEnumerable)Get(loaded, "ResetEligibleZones")) == "0,0;1,0", "Manifest round trip preserves eligible subset");
                Check(Get(loaded, "ResetCompletedZones") != null && ((IList)Get(loaded, "ResetCompletedZones")).Count == 0,
                    "Fresh archive persists an explicit empty completed list");
                Check(Get(loaded, "ResetPendingZone") == null, "Fresh archive has no ambiguous pending reset");
                if (reset) Rejected(delegate { RestoreKeys(loaded); }, "Reloaded fresh reset archive still cannot replace unreset structures");
            }
            string pendingPath = Path.Combine(directory, "pending.yml");
            object pending = Manifest(3, true, 0, 0);
            Set(pending, "ResetPendingZone", ModelZone(1, 0));
            Call(serialization, "SaveManifest", pendingPath, pending);
            object loadedPending = Call(serialization, "LoadManifest", pendingPath);
            Check(ZoneKey(Get(loadedPending, "ResetPendingZone")) == "1,0", "Crash journal survives disk round trip");
            Rejected(delegate { RestoreKeys(loadedPending); }, "Disk-loaded pending journal still blocks restore");
            string legacyPath = Path.Combine(directory, "legacy.yml");
            Call(serialization, "SaveManifest", legacyPath, Manifest(2, false));
            Check(RestoreKeys(Call(serialization, "LoadManifest", legacyPath)) == "0,0;1,0;2,0", "Legacy v2 manifest survives unchanged round trip");
        }
        finally
        {
            // Every file is fixture-owned, under this unique directory; never recurse into game data.
            foreach (string path in Directory.GetFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
        System.Console.WriteLine("PASS archive manifest validation and disk round trips: explicit reset scope, intent and durable partial-reset state");
    }
    public static void Run(string assemblyPath, string dependencyDirectory)
    {
        Dictionary<string, Assembly> dependencies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        ResolveEventHandler resolver = delegate(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            Assembly loaded;
            if (dependencies.TryGetValue(name, out loaded)) return loaded;
            string path = Path.Combine(dependencyDirectory, name + ".dll");
            if (!File.Exists(path)) return null;
            loaded = Assembly.Load(File.ReadAllBytes(path));
            dependencies.Add(name, loaded);
            return loaded;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        try
        {
            Assembly mod = Assembly.LoadFrom(assemblyPath);
            store = mod.GetType("ZoneSavior.ZoneBundleStore", true);
            serialization = mod.GetType("ZoneSavior.ZoneBundleSerialization", true);
            scanner = mod.GetType("ZoneSavior.AutoArchiveScanner", true);
            infoType = scanner.GetNestedType("AutoArchiveZoneInfo", BindingFlags.NonPublic);
            zoneType = infoType.GetProperty("Zone").PropertyType;
            manifestType = mod.GetType("ZoneSavior.ZoneBundleManifest", true);
            entryType = mod.GetType("ZoneSavior.ZoneBundleManifestEntry", true);
            modelZoneType = mod.GetType("ZoneSavior.ZoneBundleZone", true);
            assertions = 0;
            Planning();
            RestoreSelection();
            ManifestValidation();
            System.Console.WriteLine("PASS " + assertions + " auto archive regression assertions: " + assemblyPath);
        }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
    }
}
'@

try { [AutoArchiveRegression]::Run($resolvedAssembly, $resolvedDependencies) }
catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }

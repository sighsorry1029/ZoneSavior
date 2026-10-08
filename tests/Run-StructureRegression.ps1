#requires -Version 5.1
param(
    [string] $AssemblyPath = (Join-Path $PSScriptRoot '../bin/ZoneSavior/Debug/ZoneSavior.dll'),
    [string] $DependencyDirectory = (Join-Path $PSScriptRoot '../bin/ZoneSavior/Debug')
)

# Run each assembly in a separate Windows PowerShell process to avoid assembly reuse.
# These tests only create managed records/dictionaries; no plugin, world, or file initialization runs.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    throw 'Use Windows PowerShell (powershell.exe), which runs the game assembly on .NET Framework.'
}
$resolvedAssembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
$resolvedDependencies = (Resolve-Path -LiteralPath $DependencyDirectory).Path

Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class StructureRegression
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type store, recordType, stateType;
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }
    private static object Get(object instance, string property)
    {
        return instance.GetType().GetProperty(property).GetValue(instance, null);
    }
    private static void Set(object instance, string property, object value)
    {
        instance.GetType().GetProperty(property).SetValue(instance, value, null);
    }
    private static object Call(Type type, string method, params object[] args)
    {
        return type.GetMethod(method, Static).Invoke(null, args);
    }
    private static object Record(string platform, long[] ids, string[] names, DateTime seen)
    {
        object record = Activator.CreateInstance(recordType, true);
        Set(record, "PlatformId", platform);
        Set(record, "PlayerIds", new List<long>(ids));
        Set(record, "Names", new List<string>(names));
        Set(record, "LastSeenUtc", seen);
        return record;
    }
    private static object State(params object[] records)
    {
        object state = Activator.CreateInstance(stateType, true);
        IList players = (IList)Get(state, "Players");
        foreach (object record in records) players.Add(record);
        return state;
    }
    private static void Sequence(object record, string property, params object[] expected)
    {
        IList actual = (IList)Get(record, property);
        Check(actual.Count == expected.Length, property + " count");
        for (int i = 0; i < expected.Length; i++)
            Check(Object.Equals(actual[i], expected[i]), property + " order at " + i);
    }
    private static void RuntimeMerge(DateTime now)
    {
        foreach (int offset in new[] { -1, 0, 1 })
        {
            object target = Record("local:1", new long[] { 1, 2 }, new[] { "A", "B" }, now);
            object duplicate = Record("steam:76561198000000000", new long[] { 2, 3 }, new[] { "B", "C", "A" }, now.AddDays(offset));
            object state = State(target, duplicate);
            store.GetProperty("State", Static).GetSetMethod(true).Invoke(null, new[] { state });
            Call(store, "MergePlayerRecords", target, duplicate);
            IList players = (IList)Get(state, "Players");
            Check(players.Count == 1 && Object.ReferenceEquals(players[0], target), "Runtime preserves target identity");
            Check((string)Get(target, "PlatformId") == "local:1", "Runtime preserves target platform");
            Sequence(target, "PlayerIds", 1L, 2L, 3L);
            Sequence(target, "Names", "A", "B", "C");
            Check((DateTime)Get(target, "LastSeenUtc") == now.AddDays(Math.Max(0, offset)), "Runtime max timestamp");
            Sequence(duplicate, "PlayerIds", 2L, 3L);
            Sequence(duplicate, "Names", "B", "C", "A");
        }
        System.Console.WriteLine("PASS runtime merge: identity, platform, ordered unions, timestamps, source unchanged");
    }
    private static void Normalization(DateTime now)
    {
        object unknown = Record("unknown:1", new long[] { 1, 2 }, new[] { "unknown", "Old" }, now.AddDays(1));
        object steam = Record("steam:76561198000000000", new long[] { 2, 3 }, new[] { "New", "Old" }, now);
        object state = State(unknown, steam);
        Call(store, "NormalizePlayerRecords", state);
        IList players = (IList)Get(state, "Players");
        Check(players.Count == 1 && Object.ReferenceEquals(players[0], steam), "Reload prefers Steam identity");
        Sequence(steam, "PlayerIds", 2L, 3L, 1L);
        Sequence(steam, "Names", "New", "Old", "unknown");
        Check((DateTime)Get(steam, "LastSeenUtc") == now.AddDays(1), "Reload preserves latest timestamp");

        object first = Record("local:4", new long[] { 4 }, new[] { "First" }, now);
        object second = Record("session:5", new long[] { 4, 5 }, new[] { "Second" }, now);
        object tied = State(first, second);
        Call(store, "NormalizePlayerRecords", tied);
        Check(Object.ReferenceEquals(((IList)Get(tied, "Players"))[0], first), "Equal priority preserves first identity");
        Sequence(first, "Names", "First", "Second");

        object zeroA = Record("unknown:0", new long[] { 0, 6 }, new[] { "ZeroA" }, now);
        object zeroB = Record("local:7", new long[] { 0, 7 }, new[] { "ZeroB" }, now);
        object zeros = State(zeroA, zeroB);
        Call(store, "NormalizePlayerRecords", zeros);
        Check(((IList)Get(zeros, "Players")).Count == 2, "Shared zero ID alone must not merge");
        System.Console.WriteLine("PASS reload normalization: Steam preference, stable ties, zero-ID exclusion");
    }
    private static void ChainAndIndexes(DateTime now)
    {
        object a = Record("unknown:1", new long[] { 1, 2 }, new[] { "A" }, now.AddDays(2));
        object b = Record("local:2", new long[] { 2, 3 }, new[] { "B" }, now.AddDays(1));
        object c = Record("steam:76561198000000000", new long[] { 3, 4 }, new[] { "C" }, now);
        object state = State(a, b, c);
        Call(store, "NormalizePlayerRecords", state);
        IList players = (IList)Get(state, "Players");
        Check(players.Count == 1 && Object.ReferenceEquals(players[0], c), "Transitive overlap produces one canonical record");
        Sequence(c, "PlayerIds", 3L, 4L, 2L, 1L);
        Sequence(c, "Names", "C", "B", "A");
        Check((DateTime)Get(c, "LastSeenUtc") == now.AddDays(2), "Chain preserves latest timestamp");
        Call(store, "NormalizePlayerRecords", state);
        Sequence(c, "PlayerIds", 3L, 4L, 2L, 1L);
        Sequence(c, "Names", "C", "B", "A");
        Call(store, "ReplaceState", state);
        IDictionary byId = (IDictionary)store.GetField("PlayersById", Static).GetValue(null);
        IDictionary byPlatform = (IDictionary)store.GetField("PlayersByPlatform", Static).GetValue(null);
        Check(byId.Count == 4 && byPlatform.Count == 1, "Replacement builds exact index sizes");
        foreach (long id in new long[] { 1, 2, 3, 4 })
            Check(Object.ReferenceEquals(byId[id], c), "ID index uses canonical record: " + id);
        Check(Object.ReferenceEquals(byPlatform[Get(c, "PlatformId")], c), "Platform index uses canonical record");
        System.Console.WriteLine("PASS transitive overlap, normalization idempotence, replacement indexes");
    }
    private static void Grace(Assembly assembly, DateTime now)
    {
        Type grace = assembly.GetType("ZoneSavior.ZoneBundleSupportGrace", true);
        IDictionary entries = (IDictionary)grace.GetField("GraceUntilUtc", Static).GetValue(null);
        Type zoneType = entries.GetType().GetGenericArguments()[0];
        object past = Activator.CreateInstance(zoneType, new object[] { -1, 2 });
        object boundary = Activator.CreateInstance(zoneType, new object[] { 0, 0 });
        object future = Activator.CreateInstance(zoneType, new object[] { 3, -4 });
        entries.Clear();
        Call(grace, "RemoveExpired", now);
        Check(entries.Count == 0, "Empty grace table stays empty");
        entries[future] = now.AddTicks(1);
        Call(grace, "RemoveExpired", now);
        Check(entries.Count == 1 && entries.Contains(future), "Future grace survives cleanup");
        entries[past] = now.AddTicks(-1);
        entries[boundary] = now;
        Call(grace, "RemoveExpired", now);
        Check(entries.Count == 1 && entries.Contains(future), "Expired and exact-boundary grace removed");
        Check((DateTime)entries[future] == now.AddTicks(1), "Cleanup preserves live deadline");
        entries[past] = now.AddDays(1);
        Call(grace, "RemoveExpired", now);
        Check(entries.Count == 2 && entries.Contains(past), "Later cleanup cannot reuse stale removal entries");
        Call(grace, "RemoveExpired", now.AddTicks(1));
        Check(entries.Count == 1 && entries.Contains(past), "Repeated cleanup expires next boundary only");
        entries.Clear();
        System.Console.WriteLine("PASS grace cleanup: empty, future, expiry boundary, repeated cleanup");
    }
    private static void ArchiveResults(Assembly assembly)
    {
        Type commands = assembly.GetType("ZoneSavior.ZoneBundleCommands", true);
        Type progressType = commands.GetNestedType("ArchiveSaveProgress", BindingFlags.NonPublic);
        Type manifestType = assembly.GetType("ZoneSavior.ZoneBundleManifest", true);
        MethodInfo create = commands.GetMethod("CreateArchiveResult", Static, null,
            new[] { typeof(bool), typeof(string), typeof(string), manifestType, progressType, typeof(string) }, null);
        object progress = Activator.CreateInstance(progressType, true);
        string[] fields = { "TotalEntries", "TotalMonsters", "TerrainLoaded", "TerrainCaptured" };
        string[] resultFields = { "EntryCount", "MonsterCount", "TerrainLoaded", "TerrainCaptured" };
        int[] values = { 17, 3, 2, 1 };
        for (int i = 0; i < fields.Length; i++) progressType.GetField(fields[i]).SetValue(progress, values[i]);
        foreach (int count in new[] { 0, 2 })
        {
            object manifest = Activator.CreateInstance(manifestType, true);
            IList bundles = (IList)Get(manifest, "Bundles");
            for (int i = 0; i < count; i++) bundles.Add(Activator.CreateInstance(assembly.GetType("ZoneSavior.ZoneBundleManifestEntry", true), true));
            foreach (string message in new string[] { null, "save failed", "" })
            {
                bool success = message != "save failed";
                object result = create.Invoke(null, new object[] { success, "test-tag", @"C:\managed-test\manifest.yml", manifest, progress, message });
                Check((bool)Get(result, "Success") == success, "Archive result success flag");
                Check((string)Get(result, "Tag") == "test-tag", "Archive result tag");
                Check((string)Get(result, "ManifestPath") == @"C:\managed-test\manifest.yml", "Archive result path");
                Check((int)Get(result, "ZoneCount") == count, "Archive result zone count");
                for (int i = 0; i < values.Length; i++) Check((int)Get(result, resultFields[i]) == values[i], "Archive result " + resultFields[i]);
                string expected = message ?? "Saved " + count + " zone bundle(s) for tag 'test-tag' to 'C:\\managed-test' " +
                    "(entries: 17, monsters: 3, terrain contacts: 1/" + count + ", terrain loaded: 2/" + count + ", mode: SupportFill).";
                Check((string)Get(result, "Message") == expected, "Archive result default/error/empty message");
            }
        }
        System.Console.WriteLine("PASS archive results: counts, success/error, default/empty message, empty manifest");
    }
    public static void Run(string assemblyPath, string dependencyDirectory)
    {
        Dictionary<string, Assembly> dependencies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        ResolveEventHandler resolver = delegate(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            Assembly loaded;
            if (dependencies.TryGetValue(name, out loaded)) return loaded;
            foreach (string suffix in new[] { ".dll", "_publicized.dll" })
            {
                string path = Path.Combine(dependencyDirectory, name + suffix);
                if (!File.Exists(path)) continue;
                // Read known local test dependencies without changing their download-origin metadata.
                loaded = Assembly.Load(File.ReadAllBytes(path));
                dependencies.Add(name, loaded);
                return loaded;
            }
            return null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        try
        {
            Assembly assembly = Assembly.LoadFrom(assemblyPath);
            store = assembly.GetType("ZoneSavior.AutoArchiveStore", true);
            recordType = assembly.GetType("ZoneSavior.PlayerActivityRecord", true);
            stateType = assembly.GetType("ZoneSavior.AutoArchiveState", true);
            DateTime now = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
            RuntimeMerge(now);
            Normalization(now);
            ChainAndIndexes(now);
            Grace(assembly, now);
            ArchiveResults(assembly);
            System.Console.WriteLine("PASS " + assertions + " assertions: " + assemblyPath);
        }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
    }
}
'@

try {
    [StructureRegression]::Run($resolvedAssembly, $resolvedDependencies)
}
catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}

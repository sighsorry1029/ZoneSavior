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
    private static void CharacterNames(Assembly assembly, DateTime now)
    {
        object legacy = Record("steam:76561198000000000", new long[] { 101, 102 }, new[] { "OldA", "OldB" }, now);
        object state = State(legacy);
        Call(store, "ReplaceState", state);
        CheckName(101, null, "Legacy account aliases do not identify a character");
        CheckName(102, null, "A shared account cannot assign its last alias to every character");
        Call(store, "RecordUnknownPlayer", 103L, now, "UnverifiedPieceName");
        CheckName(103, null, "Scanner piece metadata cannot populate exact character names");
        Call(store, "RecordPlayerSeen", "steam:76561198000000000", 101L, "Viking A", now.AddMinutes(1));
        Call(store, "RecordPlayerSeen", "steam:76561198000000000", 102L, "Viking B", now.AddMinutes(2));
        CheckName(101, "Viking A", "Second character login preserves first character name");
        CheckName(102, "Viking B", "Second character has an independent name");
        Call(store, "RecordPlayerSeen", "steam:76561198000000000", 101L, "Renamed A", now.AddMinutes(3));
        CheckName(101, "Renamed A", "Rename replaces last observed character name");
        Call(store, "RecordPlayerSeen", "steam:76561198000000000", 101L, "Viking A", now.AddMinutes(4));
        CheckName(101, "Viking A", "A to B to A updates even when the account already contains that alias");
        Call(store, "RecordPlayerSeen", "steam:76561198000000000", 101L, "  ", now.AddMinutes(5));
        CheckName(101, "Viking A", "Empty handshake name cannot erase a known character");
        Call(store, "RecordPlayerSeen", "steam:76561198000000000", 0L, "NoId", now.AddMinutes(6));
        CheckName(0, null, "Zero player ID cannot acquire a character name");
        Sequence(legacy, "PlayerIds", 101L, 102L);
        Sequence(legacy, "Names", "OldA", "OldB", "Viking A", "Viking B", "Renamed A", "  ", "NoId");
        Check((DateTime)Get(legacy, "LastSeenUtc") == now.AddMinutes(6), "Account-level activity remains shared across characters");
        object[] accountQuery = { "76561198000000000", null, null };
        Check((bool)store.GetMethod("TryGetPlayerIdsBySteamId", Static).Invoke(null, accountQuery), "Steam account lookup survives exact-name mapping");
        Check(((IList)accountQuery[1]).Count == 2, "Steam targeting still covers both characters");

        // A scanner-only record may later become a verified account record. Its
        // old alias remains history but must never overwrite the connected name.
        Call(store, "RecordPlayerSeen", "steam:76561198000000000", 103L, "Observed C", now.AddMinutes(7));
        CheckName(103, "Observed C", "Account merge records an actual character observation");
        CheckName(101, "Viking A", "Account merge preserves unrelated character mappings");
        Call(store, "RecordUnknownPlayer", 103L, now.AddMinutes(8), "AnotherPieceAlias");
        CheckName(103, "Observed C", "Scanner alias cannot overwrite an observed character");
        Call(store, "RecordPlayerSeen", "steam:76561198000000000", 104L, "manual", now.AddMinutes(9));
        CheckName(104, "manual", "An actually observed character named manual is not an alias sentinel");

        Type commands = assembly.GetType("ZoneSavior.ZoneBundleCommands", true);
        var observed = new Dictionary<long, string> { { 101, "WrongPieceName" }, { 999, "UnverifiedPieceName" } };
        IList creators = (IList)Call(commands, "BuildSourceZoneCreators", observed);
        Check((string)Get(creators[0], "Name") == "Viking A", "New bundle metadata prefers exact character mapping over piece text");
        Check(Get(creators[1], "Name") == null, "New bundle metadata cannot infer an unobserved character name");
        CheckName(999, null, "Bundle capture cannot seed character mapping from piece text");
        Set(creators[1], "Name", "HistoricalBundleName");
        object clone = Call(commands, "CloneCreatorPlayer", creators[1]);
        Check((string)Get(clone, "Name") == "HistoricalBundleName", "Existing bundle metadata remains preserved on clone/restore");
        Type scanner = assembly.GetType("ZoneSavior.AutoArchiveScanner", true);
        Check((string)Call(scanner, "ResolvePlayerName", 102L) == "Viking_B", "Archive token uses the correct character");
        Check((string)Call(scanner, "ResolvePlayerName", 999L) == "unknown", "Archive token fails closed without observation");

        FieldInfo serializerField = store.GetField("Serializer", Static);
        FieldInfo deserializerField = store.GetField("Deserializer", Static);
        MethodInfo serialize = serializerField.FieldType.GetMethod("Serialize", new[] { typeof(object) });
        MethodInfo deserialize = deserializerField.FieldType.GetMethod("Deserialize", new[] { typeof(string), typeof(Type) });
        string yaml = (string)serialize.Invoke(serializerField.GetValue(null), new[] { state });
        Check(yaml.Contains("character_names:"), "Exact-name map has a separate YAML field");
        object roundTrip = deserialize.Invoke(deserializerField.GetValue(null), new object[] { yaml, stateType });
        Call(store, "ValidateState", roundTrip);
        Call(store, "ReplaceState", roundTrip);
        CheckName(101, "Viking A", "Exact name survives YAML round trip");
        CheckName(102, "Viking B", "Same-account second name survives YAML round trip");
        Check(((IList)Get(roundTrip, "Players")).Count == ((IList)Get(state, "Players")).Count, "YAML preserves account history");
        string oldYaml = "version: 1\nplayers:\n- platform_id: steam:76561198000000000\n  player_ids: [101, 102]\n  names: [OldA, OldB]\nruns: []\n";
        object oldState = deserialize.Invoke(deserializerField.GetValue(null), new object[] { oldYaml, stateType });
        Call(store, "ValidateState", oldState);
        Call(store, "ReplaceState", oldState);
        CheckName(101, null, "Old YAML remains readable without guessing a character mapping");
        Sequence(((IList)Get(oldState, "Players"))[0], "Names", "OldA", "OldB");
        Check(((IDictionary)Get(oldState, "CharacterNames")).Count == 0, "Old YAML does not backfill names from account aliases");
        Set(oldState, "CharacterNames", null);
        Call(store, "ReplaceState", oldState);
        CheckName(101, null, "Explicitly absent optional name map does not discard account history");
        System.Console.WriteLine("PASS character identity: independent names, rename, legacy isolation, account preservation, bundle labels and YAML round trip");
    }
    private static void CheckName(long id, string expected, string message)
    {
        object[] args = { id, null };
        bool found = (bool)store.GetMethod("TryResolveLastKnownPlayerName", Static).Invoke(null, args);
        Check(found == (expected != null) && (string)args[1] == (expected ?? ""), message);
    }
    private static void CreatorProtocol(Assembly assembly, DateTime now)
    {
        Type protocol = assembly.GetType("ZoneSavior.ZoneSaviorCreatorNameLookupProtocol", true);
        Type rpc = assembly.GetType("ZoneSavior.CreatorNameLookupRpc", true);
        Check((int)protocol.GetField("Version").GetRawConstantValue() == 2, "Current protocol advertises exact character semantics");
        foreach (int version in new[] { -1, 0, 1, 2, 3, Int32.MaxValue })
            Check((bool)Call(rpc, "IsSupportedVersion", version) == (version == 1 || version == 2), "Only wire versions 1 and 2 are supported: " + version);
        foreach (int version in new[] { 1, 2 })
        foreach (bool found in new[] { false, true })
        {
            string name = found ? "Viking A" : "";
            object response = Call(rpc, "CreateResponse", version, 17L, 23L, 0, 101L, found, name);
            Type package = response.GetType();
            package.GetMethod("SetPos").Invoke(response, new object[] { 0 });
            Check((int)package.GetMethod("ReadInt").Invoke(response, null) == version, "Response echoes requested protocol");
            Check((long)package.GetMethod("ReadLong").Invoke(response, null) == 17L, "Response echoes request ID");
            Check((long)package.GetMethod("ReadLong").Invoke(response, null) == 23L, "Response echoes world ID");
            Check((int)package.GetMethod("ReadInt").Invoke(response, null) == 0, "Success status retains wire order");
            Check((long)package.GetMethod("ReadLong").Invoke(response, null) == 101L, "Response echoes character ID");
            Check((bool)package.GetMethod("ReadBool").Invoke(response, null) == found, "Found flag retains wire order");
            Check((string)package.GetMethod("ReadString").Invoke(response, null) == name, "Response sends exact name or empty not-found result");
            Check((int)package.GetMethod("GetPos").Invoke(response, null) == (int)package.GetMethod("Size").Invoke(response, null), "Response has no unexpected wire fields");
        }
        Call(rpc, "ClearRateLimits");
        for (int i = 0; i < 8; i++) Check((bool)Call(rpc, "TryConsumeRequest", 7L, now), "Rate window permits bounded request " + i);
        Check(!(bool)Call(rpc, "TryConsumeRequest", 7L, now), "Ninth lookup is still rate limited");
        Check((bool)Call(rpc, "TryConsumeRequest", 7L, now.AddSeconds(5)), "Rate limit resets at existing window boundary");
        Call(rpc, "ClearRateLimits");
        System.Console.WriteLine("PASS creator lookup protocol: v1/v2 support and echo, exact wire layout, unchanged rate limits");
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
            CharacterNames(assembly, now);
            CreatorProtocol(assembly, now);
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

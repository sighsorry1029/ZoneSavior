#requires -Version 5.1
param(
    [string] $AssemblyPath = (Join-Path $PSScriptRoot '../bin/ZoneSavior/Debug/ZoneSavior.dll'),
    [string] $DependencyDirectory = (Join-Path $PSScriptRoot '../bin/ZoneSavior/Debug')
)

# Managed DLL tests: injected terrain queries, no Unity/game initialization.
# Gzip fixtures use a unique temporary directory, never a BepInEx profile or world.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this script with Windows PowerShell (powershell.exe).' }
$resolvedAssembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
$resolvedDependencies = (Resolve-Path -LiteralPath $DependencyDirectory).Path

Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

public static class TerrainPlacementRegression
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static Type terrain, sampleType, bundleType, contactType, entryType, responseType, serialization;
    private static MethodInfo resolve;
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }
    private static void Near(float expected, float actual, string message)
    {
        Check(!float.IsNaN(actual) && Math.Abs(expected - actual) < 0.0001f,
            message + ": expected " + expected + ", actual " + actual);
    }
    private static object Get(object value, string property) { return value.GetType().GetProperty(property).GetValue(value, null); }
    private static void Set(object value, string property, object data) { value.GetType().GetProperty(property).SetValue(value, data, null); }
    private static object Call(Type type, string method, params object[] args) { return type.GetMethod(method, Static).Invoke(null, args); }
    private static IList Samples(params float[][] rows)
    {
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sampleType));
        foreach (float[] row in rows) list.Add(Activator.CreateInstance(sampleType, new object[] { row[0], row[1], row[2] }));
        return list;
    }
    private static float Solve(IList samples, float[] heights, float minimumOffset, Func<float, float, float?> query, bool expectedSuccess = true)
    {
        object[] original = new object[samples.Count];
        samples.CopyTo(original, 0);
        List<float> votes = new List<float>(heights);
        object[] args = { samples, votes, minimumOffset, query, 0f };
        Check((bool)resolve.Invoke(null, args) == expectedSuccess, "Representative resolution success/fallback decision");
        float result = (float)args[4];
        if (!expectedSuccess) Near(0f, result, "Failed resolution leaves neutral output");
        Check(samples.Count == original.Length, "Solver preserves sample count");
        for (int i = 0; i < original.Length; i++) Check(Object.Equals(original[i], samples[i]), "Solver preserves sample value/order " + i);
        Check(votes.Count == heights.Length, "Solver preserves piece vote count");
        for (int i = 0; i < heights.Length; i++) Check(votes[i].Equals(heights[i]), "Solver preserves piece height/order " + i);
        return result;
    }
    private static void Placement()
    {
        IList diagram = Samples(new[] { 0f, 0f, 1f }, new[] { 2f, 0f, 1f }, new[] { 4f, 0f, 1f }, new[] { 12f, 0f, 0f }, new[] { 24f, 0f, 14f });
        float[] diagramVotes = { 1f, 1f, 1f, 0f, 14f };
        Near(5f, Solve(diagram, diagramVotes, -7f, delegate(float x, float z) { return 13f - 0.5f * x; }), "Reversed slope with saved cut depth");
        Near(15f, Solve(diagram, diagramVotes, -7f, delegate(float x, float z) { return 21f + 0.5f * x; }), "Similar slope translated upward");
        Near(12f, Solve(diagram, diagramVotes, 0f, delegate(float x, float z) { return 13f - 0.5f * x; }), "No saved cutting depth");
        Console.WriteLine("PASS slope examples: reversed=5, translated=15, zero offset=12; inputs unchanged");

        IList mode = Samples(new[] { 0f, 0f, 0f }, new[] { 1f, 0f, 0f }, new[] { 2f, 0f, 0f }, new[] { 3f, 0f, 5f }, new[] { 4f, 0f, 5f }, new[] { 5f, 0f, 10f }, new[] { 6f, 0f, 10f });
        Near(20f, Solve(mode, new[] { 0f, 0f, 0f, 5f, 5f, 10f, 10f }, 0f, delegate(float x, float z) { return 20f; }), "Modal support layer wins over median layer");
        IList firstContact = Samples(new[] { 0f, 0f, 2f }, new[] { 1f, 0f, 2f }, new[] { 2f, 0f, 2f });
        Near(28f, Solve(firstContact, new[] { 2f }, 0f, delegate(float x, float z) { return x == 1f ? 30f : (x == 0f ? 10f : 20f); }), "First contact uses maximum offset, not median");
        IList ties = Samples(new[] { 0f, 0f, 5f }, new[] { 1f, 0f, 1f });
        Func<float, float, float?> tiedQuery = delegate(float x, float z) { return x == 0f ? 100f : 10f; };
        Near(9f, Solve(ties, new[] { 5f, 1f }, 0f, tiedQuery), "Equal counts prefer lower layer");
        Near(9f, Solve(Samples(new[] { 1f, 0f, 1f }, new[] { 0f, 0f, 5f }), new[] { 1f, 5f }, 0f, tiedQuery), "Tie is input-order independent");
        IList quantized = Samples(new[] { 0f, 0f, 0.89f }, new[] { 1f, 0f, 1.01f }, new[] { 2f, 0f, 1.10f }, new[] { 3f, 0f, 5f });
        Near(10.99f, Solve(quantized, new[] { 0.89f, 1.01f, 1.10f, 5f }, 0f, delegate(float x, float z) { return x == 3f ? 100f : (x == 1f ? 12f : 11f); }), "Quarter-meter grouping retains each sample's actual height");
        IList halfSteps = Samples(new[] { 0f, 0f, 0.875f }, new[] { 1f, 0f, 1.125f }, new[] { 2f, 0f, 1.25f });
        Near(19.125f, Solve(halfSteps, new[] { 0.875f, 1.125f, 1.25f }, 0f, delegate(float x, float z) { return 20f; }), "Exact half-bin ties preserve quarter-meter rounding and actual point height");
        IList stacked = Samples(new[] { 0f, 0f, 0.6f }, new[] { 0f, 0f, 1f }, new[] { 10f, 0f, 1f });
        Near(12f, Solve(stacked, new[] { 1f, 1f, 1f, 0.6f }, -7f, delegate(float x, float z) { return x == 0f ? 20f : 0f; }), "Overlapping XZ contacts retain the representative upper plane");
        List<float[]> unequalFootprints = new List<float[]>();
        List<float> pieceVotes = new List<float>();
        for (int i = 0; i < 10; i++) { unequalFootprints.Add(new[] { (float)i, 0f, 1f }); pieceVotes.Add(1f); }
        for (int i = 0; i < 100; i++) unequalFootprints.Add(new[] { 100f + i, 0f, 0f });
        pieceVotes.Add(0f);
        Near(9f, Solve(Samples(unequalFootprints.ToArray()), pieceVotes.ToArray(), 0f, delegate(float x, float z) { return 10f; }), "Ten small pieces outweigh one large piece with 100 contact cells");
        Console.WriteLine("PASS modal piece layer, maximum contact, stable ties, quantization, one vote per piece");

        IList partial = Samples(new[] { 0f, 0f, 1f }, new[] { 1f, 0f, 1f }, new[] { 2f, 0f, 1f }, new[] { 3f, 0f, 9f });
        float[] partialVotes = { 1f, 1f, 1f, 9f };
        Solve(partial, partialVotes, 0f, delegate(float x, float z) { return x == 0f ? (float?)null : 14f; }, false);
        Solve(partial, partialVotes, -7f, delegate(float x, float z) { return null; }, false);
        Near(13f, Solve(partial, partialVotes, 0f, delegate(float x, float z) { return x == 3f ? (float?)null : 14f; }), "Missing native height on non-selected plane is irrelevant");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Solve(partial, partialVotes, 0f, delegate(float x, float z) { return invalid; }, false);
        foreach (float invalid in new[] { 0.01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Solve(partial, partialVotes, invalid, delegate(float x, float z) { return 14f; }, false);
        Func<float, float, float?> noQuery = delegate(float x, float z) { throw new Exception("Unsupported input queried terrain"); };
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Solve(partial, new[] { invalid }, 0f, noQuery, false);
        Solve(Samples(new[] { 0f, 0f, -float.MaxValue }), new[] { -float.MaxValue }, 0f,
            delegate(float x, float z) { return float.MaxValue; }, false);
        Solve(Samples(), new[] { 1f }, -7f, noQuery, false);
        Solve(partial, new float[0], 0f, noQuery, false);
        Solve(Samples(new[] { 0f, 0f, 5f }), new[] { 1f }, 0f, noQuery, false);
        Console.WriteLine("PASS unavailable/invalid native heights, invalid offsets, missing samples/votes/selected plane return fallback");
    }
    private static int callbackCount;
    public static void CaptureContext(object context) { callbackCount++; }
    private static object IteratorLocal(IEnumerator iterator, string name)
    {
        foreach (FieldInfo field in iterator.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (field.Name.StartsWith("<" + name + ">", StringComparison.Ordinal)) return field.GetValue(iterator);
        throw new MissingFieldException("Update the iterator observation for local " + name);
    }
    private static void NextTarget(IEnumerator iterator)
    {
        while (true)
        {
            Check(iterator.MoveNext(), "Placement coroutine reaches next target boundary");
            IEnumerator nested = iterator.Current as IEnumerator;
            if (nested == null) break;
            try { Check(!nested.MoveNext(), "Empty fallback target needs no native work"); }
            finally { ((IDisposable)nested).Dispose(); }
        }
        Check(iterator.Current == null && callbackCount == 0, "Target boundary precedes final native placement queries");
    }
    private static void AggregateState(IEnumerator iterator, int sampleCount, float[] votes, float minimum, bool complete)
    {
        Check(((IList)IteratorLocal(iterator, "allSamples")).Count == sampleCount, "All support samples aggregate across targets");
        IList actualVotes = (IList)IteratorLocal(iterator, "supportPieceRelativeHeights");
        Check(actualVotes.Count == votes.Length, "Piece vote count aggregates across targets");
        for (int i = 0; i < votes.Length; i++) Near(votes[i], (float)actualVotes[i], "Aggregated relative piece height " + i);
        Near(minimum, (float)IteratorLocal(iterator, "minimumTerrainOffset"), "Entire archive shares minimum saved offset");
        Check((bool)IteratorLocal(iterator, "hasCompletePlacementMetadata") == complete, "Whole-load legacy fallback selection");
    }
    private static object SupportTarget(Type targetType, int x, float? offset, float[] votes, bool hasContact)
    {
        object target = Activator.CreateInstance(targetType, true);
        Type zoneType = targetType.GetProperty("Zone").PropertyType;
        Set(target, "Zone", Activator.CreateInstance(zoneType, new object[] { x, 0 }));
        Set(target, "SourceBaseY", 30f); Set(target, "MinimumTerrainOffset", offset);
        Set(target, "SupportPieceRelativeHeights", votes == null ? null : new List<float>(votes));
        Set(target, "ContactsCaptured", hasContact);
        if (hasContact) ((IList)Get(target, "Contacts")).Add(Contact(1f));
        return target;
    }
    private static IEnumerator StartTargets(Assembly assembly, IList targets)
    {
        callbackCount = 0;
        Type callbackType = typeof(Action<>).MakeGenericType(assembly.GetType("ZoneSavior.TerrainPlacementContext", true));
        Delegate callback = Delegate.CreateDelegate(callbackType, typeof(TerrainPlacementRegression).GetMethod("CaptureContext"));
        return (IEnumerator)Call(terrain, "CreateSupportFillPlacementContextAsync", targets, callback);
    }
    private static void Aggregation(Assembly assembly)
    {
        Type targetType = assembly.GetType("ZoneSavior.TerrainSupportTarget", true);
        Type listType = typeof(List<>).MakeGenericType(targetType);
        IList complete = (IList)Activator.CreateInstance(listType);
        complete.Add(SupportTarget(targetType, 0, -2f, new[] { 1f, 1f }, true));
        complete.Add(SupportTarget(targetType, 1, -7f, new[] { 4f }, true));
        complete.Add(SupportTarget(targetType, 2, null, null, false));
        IEnumerator iterator = StartTargets(assembly, complete);
        try
        {
            NextTarget(iterator); AggregateState(iterator, 1, new[] { 1f, 1f }, -2f, true);
            NextTarget(iterator); AggregateState(iterator, 2, new[] { 1f, 1f, 4f }, -7f, true);
            NextTarget(iterator); AggregateState(iterator, 2, new[] { 1f, 1f, 4f }, -7f, true);
        }
        finally { ((IDisposable)iterator).Dispose(); }
        foreach (float[] legacyVotes in new float[][] { null, new[] { 9f } })
        {
            IList mixed = (IList)Activator.CreateInstance(listType);
            mixed.Add(SupportTarget(targetType, 0, -2f, new[] { 1f }, true));
            mixed.Add(SupportTarget(targetType, 1, null, legacyVotes, true));
            mixed.Add(SupportTarget(targetType, 2, -7f, new[] { 4f }, true));
            iterator = StartTargets(assembly, mixed);
            try
            {
                NextTarget(iterator); AggregateState(iterator, 1, new[] { 1f }, -2f, true);
                NextTarget(iterator); AggregateState(iterator, 2, new[] { 1f }, -2f, false);
                NextTarget(iterator); AggregateState(iterator, 3, new[] { 1f, 4f }, -7f, false);
            }
            finally { ((IDisposable)iterator).Dispose(); }
        }
        Console.WriteLine("PASS coroutine aggregation: common minimum/votes, empty target neutrality, mixed/missing-native metadata fallback flag");
    }
    private static object Contact(float relativeY)
    {
        object contact = Activator.CreateInstance(contactType, true);
        Set(contact, "LocalX", 2f); Set(contact, "LocalZ", -3f); Set(contact, "RelativeY", relativeY);
        return contact;
    }
    private static object Bundle(float? offset)
    {
        object bundle = Activator.CreateInstance(bundleType, true);
        Set(bundle, "Version", 3); Set(bundle, "Tag", "terrain-regression"); Set(bundle, "SourceBaseY", 30f);
        Set(bundle, "TerrainContactsCaptured", true); Set(bundle, "MinimumTerrainOffset", offset);
        Set(bundle, "SupportPieceRelativeHeights", new List<float> { 1f });
        ((IList)Get(bundle, "TerrainContacts")).Add(Contact(1f));
        object entry = Activator.CreateInstance(entryType, true);
        Set(entry, "Prefab", "wood_floor"); Set(entry, "LocalPos", new[] { 2f, 1f, -3f });
        Set(entry, "Rot", new[] { 0f, 0f, 0f, 1f }); Set(entry, "Scale", new[] { 1f, 1f, 1f });
        ((IList)Get(bundle, "Entries")).Add(entry);
        return bundle;
    }
    private static void Offset(float? expected, object actual, string label)
    {
        if (!expected.HasValue) Check(actual == null, label + " preserves absence");
        else { Check(actual != null, label + " preserves presence"); Near(expected.Value, (float)actual, label); }
    }
    private static string Serialize(Type type, object value)
    {
        return (string)serialization.GetMethod("Serialize", Static).MakeGenericMethod(type).Invoke(null, new[] { value });
    }
    private static object Deserialize(Type type, string yaml)
    {
        return serialization.GetMethod("Deserialize", Static).MakeGenericMethod(type).Invoke(null, new object[] { yaml });
    }
    private static string ReadGzip(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        using (GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress))
        using (StreamReader reader = new StreamReader(gzip)) return reader.ReadToEnd();
    }
    private static void WriteGzip(string path, string yaml)
    {
        using (FileStream stream = File.Create(path))
        using (GZipStream gzip = new GZipStream(stream, CompressionMode.Compress))
        using (StreamWriter writer = new StreamWriter(gzip, new UTF8Encoding(false))) writer.Write(yaml);
    }
    private static void Invalid(Action action, string label)
    {
        try { action(); }
        catch (TargetInvocationException exception)
        {
            Exception inner = exception;
            while (inner is TargetInvocationException) inner = inner.InnerException;
            Check(inner is InvalidDataException, label + " fails validation, not unrelated initialization: " + inner.GetType().Name);
            return;
        }
        throw new Exception(label + " unexpectedly accepted invalid terrain metadata");
    }
    private static void Serialization(string directory)
    {
        int index = 0;
        string validWithoutOffset = null;
        foreach (float? offset in new float?[] { -7f, 0f, null })
        {
            string path = Path.Combine(directory, "roundtrip-" + index++ + ".zonebundle.yml.gz");
            object bundle = Bundle(offset);
            Call(serialization, "SaveBundle", path, bundle);
            byte[] bytes = File.ReadAllBytes(path);
            Check(bytes[0] == 0x1f && bytes[1] == 0x8b, "Saved bundle is gzip");
            string yaml = ReadGzip(path);
            if (!offset.HasValue) validWithoutOffset = yaml;
            Check(yaml.Contains("minimumTerrainOffset:") == offset.HasValue, "Optional offset YAML presence");
            object loaded = Call(serialization, "LoadBundle", path);
            Check((int)Get(loaded, "Version") == 3, "Bundle format stays v3");
            Offset(offset, Get(loaded, "MinimumTerrainOffset"), "Gzip offset");
            IList contacts = (IList)Get(loaded, "TerrainContacts");
            Check(contacts.Count == 1 && (bool)Get(loaded, "TerrainContactsCaptured"), "Gzip preserves contact state");
            Near(1f, (float)Get(contacts[0], "RelativeY"), "Gzip preserves contact relative height");
            Near(30f, (float)Get(loaded, "SourceBaseY"), "Gzip preserves source origin");
            IList votes = (IList)Get(loaded, "SupportPieceRelativeHeights");
            Check(votes.Count == 1, "Gzip preserves one vote per piece"); Near(1f, (float)votes[0], "Gzip preserves piece height");
            Near(1f, ((float[])Get(((IList)Get(loaded, "Entries"))[0], "LocalPos"))[1], "Gzip preserves piece relative position");
            object response = Activator.CreateInstance(responseType, true);
            Set(response, "RequestId", "terrain-test"); Set(response, "Success", true);
            Set(response, "SupportPieceRelativeHeights", offset.HasValue ? new List<float> { -2f, 1f, 14f } : null);
            ((IList)Get(response, "Contacts")).Add(Contact(1f));
            object received = Deserialize(responseType, Serialize(responseType, response));
            IList receivedVotes = (IList)Get(received, "SupportPieceRelativeHeights");
            if (!offset.HasValue) Check(receivedVotes == null, "Capture RPC DTO preserves absent piece heights");
            else
            {
                Check(receivedVotes.Count == 3, "Capture RPC DTO retains piece vote count");
                Near(-2f, (float)receivedVotes[0], "Capture RPC negative relative height");
                Near(1f, (float)receivedVotes[1], "Capture RPC middle relative height");
                Near(14f, (float)receivedVotes[2], "Capture RPC high relative height");
            }
            Check((bool)Get(received, "Success") && (string)Get(received, "RequestId") == "terrain-test", "Capture RPC DTO retains result identity");
            Near(1f, (float)Get(((IList)Get(received, "Contacts"))[0], "RelativeY"), "Capture RPC DTO retains contact height");
        }
        const string legacy = "version: 3\ntag: legacy\nsourceBaseY: 30\nterrainContactsCaptured: true\nterrainContacts:\n- localX: 2\n  localZ: -3\n  relativeY: 1\nentries: []\n";
        string legacyPath = Path.Combine(directory, "legacy.zonebundle.yml.gz");
        WriteGzip(legacyPath, legacy);
        object old = Call(serialization, "LoadBundle", legacyPath);
        Check(Get(old, "MinimumTerrainOffset") == null && Get(old, "SupportPieceRelativeHeights") == null && (int)Get(old, "Version") == 3, "Existing v3 bundle without new metadata remains readable");
        object oldResponse = Deserialize(responseType, "requestId: old\nsuccess: true\ncontacts: []\n");
        Check(Get(oldResponse, "SupportPieceRelativeHeights") == null, "Capture RPC DTO without piece heights remains readable");
        Console.WriteLine("PASS gzip -7/0/absent, legacy v3 absence, capture-response YAML roundtrips");

        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.01f })
        {
            object bundle = Bundle(invalid);
            Invalid(delegate { Call(serialization, "ValidateBundle", bundle); }, "Invalid numeric offset");
            string path = Path.Combine(directory, "invalid-save.zonebundle.yml.gz");
            Invalid(delegate { Call(serialization, "SaveBundle", path, bundle); }, "Invalid offset on save");
            Check(!File.Exists(path), "Rejected save does not create archive");
        }
        foreach (string invalid in new[] { ".nan", ".inf", "-.inf", "0.01" })
        {
            string path = Path.Combine(directory, "invalid-load.zonebundle.yml.gz");
            WriteGzip(path, validWithoutOffset + "minimumTerrainOffset: " + invalid + "\n");
            Invalid(delegate { Call(serialization, "LoadBundle", path); }, "Invalid offset on load");
        }
        object noContacts = Bundle(0f); ((IList)Get(noContacts, "TerrainContacts")).Clear();
        Invalid(delegate { Call(serialization, "ValidateBundle", noContacts); }, "Offset without contacts");
        object uncaptured = Bundle(-7f); ((IList)Get(uncaptured, "TerrainContacts")).Clear(); Set(uncaptured, "TerrainContactsCaptured", false);
        Invalid(delegate { Call(serialization, "ValidateBundle", uncaptured); }, "Offset without captured terrain");
        object oldEmpty = Bundle(null); ((IList)Get(oldEmpty, "TerrainContacts")).Clear(); Set(oldEmpty, "TerrainContactsCaptured", false);
        Set(oldEmpty, "SupportPieceRelativeHeights", null);
        Call(serialization, "ValidateBundle", oldEmpty); Check(true, "Legacy absence permits uncaptured empty terrain");
        object zeroVotes = Bundle(0f); Set(zeroVotes, "SupportPieceRelativeHeights", new List<float>());
        Invalid(delegate { Call(serialization, "ValidateBundle", zeroVotes); }, "Offset without piece heights");
        object tooManyVotes = Bundle(null); Set(tooManyVotes, "SupportPieceRelativeHeights", new List<float> { 1f, 2f });
        Invalid(delegate { Call(serialization, "ValidateBundle", tooManyVotes); }, "More votes than pieces");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            object bundle = Bundle(null); Set(bundle, "SupportPieceRelativeHeights", new List<float> { invalid });
            Invalid(delegate { Call(serialization, "ValidateBundle", bundle); }, "Invalid piece height");
        }
        object capturedEmpty = Bundle(null); Set(capturedEmpty, "SupportPieceRelativeHeights", new List<float>()); ((IList)Get(capturedEmpty, "TerrainContacts")).Clear();
        Call(serialization, "ValidateBundle", capturedEmpty); Check(true, "Captured empty supports remain valid without a minimum offset");
        Console.WriteLine("PASS validators reject invalid numbers and unsupported contact state");
    }
    public static void Run(string assemblyPath, string dependencyDirectory)
    {
        Dictionary<string, Assembly> dependencies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        ResolveEventHandler resolver = delegate(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name; Assembly dependency;
            if (dependencies.TryGetValue(name, out dependency)) return dependency;
            foreach (string suffix in new[] { ".dll", "_publicized.dll" })
            {
                string path = Path.Combine(dependencyDirectory, name + suffix);
                if (!File.Exists(path)) continue;
                dependency = Assembly.Load(File.ReadAllBytes(path)); dependencies.Add(name, dependency); return dependency;
            }
            return null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        string directory = Path.Combine(Path.GetTempPath(), "zonesavior-terrain-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assembly assembly = Assembly.LoadFrom(assemblyPath);
            terrain = assembly.GetType("ZoneSavior.ZoneBundleTerrain", true);
            sampleType = terrain.GetNestedType("TerrainSupportSample", BindingFlags.NonPublic);
            bundleType = assembly.GetType("ZoneSavior.ZoneBundleFile", true);
            contactType = assembly.GetType("ZoneSavior.ZoneBundleTerrainContact", true);
            entryType = assembly.GetType("ZoneSavior.ZoneBundleEntry", true);
            responseType = assembly.GetType("ZoneSavior.ZoneBundleClientTerrainCaptureResponse", true);
            serialization = assembly.GetType("ZoneSavior.ZoneBundleSerialization", true);
            resolve = terrain.GetMethod("TryResolveRepresentativeSupportBaseWorldY", Static, null,
                new[] { typeof(List<>).MakeGenericType(sampleType), typeof(List<float>), typeof(float), typeof(Func<float, float, float?>), typeof(float).MakeByRefType() }, null);
            if (resolve == null) throw new MissingMethodException("Build the representative support placement implementation before running these tests.");
            Placement();
            Aggregation(assembly);
            Directory.CreateDirectory(directory);
            Serialization(directory);
            Console.WriteLine("PASS " + assertions + " terrain placement/serialization assertions: " + assemblyPath);
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= resolver;
            if (Directory.Exists(directory))
            {
                string full = Path.GetFullPath(directory);
                string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("zonesavior-terrain-regression-", StringComparison.Ordinal))
                    throw new IOException("Refusing cleanup outside the regression test temporary directory.");
                Directory.Delete(full, true);
            }
        }
    }
}
'@

try { [TerrainPlacementRegression]::Run($resolvedAssembly, $resolvedDependencies) }
catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }

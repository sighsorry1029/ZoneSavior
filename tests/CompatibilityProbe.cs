using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Mono.Cecil;
using UnityEngine;

namespace ZoneSavior.Tests;

public static class CompatibilityProbe
{
    private static int checks;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception(description);
        checks++;
    }

    public static int Run()
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("ZONESAVIOR_MONO_PROBE_DLL");
            string managed = Environment.GetEnvironmentVariable("ZONESAVIOR_MONO_PROBE_MANAGED");
            var mod = Assembly.LoadFrom(path);
            ManagedGameBoundary.InitializeZdo();
            Check(string.Equals(Path.GetFullPath(typeof(ZDO).Assembly.Location), Path.GetFullPath(Path.Combine(managed, "assembly_valheim.dll")), StringComparison.OrdinalIgnoreCase), "Load original game DLL");
            System.Console.WriteLine("Original game: " + typeof(ZDO).Assembly.Location);
            CheckReferences(path, managed);
            foreach (string name in new[] { "ZoneSavior.ZoneSaviorGameAccess", "ZoneSavior.ZoneBundleTerrain" })
            {
                RuntimeHelpers.RunClassConstructor(mod.GetType(name, true).TypeHandle);
                Check(true, "Cached private access binding: " + name);
            }
            CheckPatches(mod);
            BuildRulesRegression.Run(mod);
            CheckSyncedValue(mod);
            CheckZdoAndZones(mod);
            Terrain1030Regression.Run(mod);
            CheckResetSnapshots(mod);
            StructureRegression.Run(path, Path.GetDirectoryName(path));
            TerrainPlacementRegression.Run(path, Path.GetDirectoryName(path));
            AutoArchiveRegression.Run(path, Path.GetDirectoryName(path));
            System.Console.WriteLine("PASS " + checks + " compatibility checks plus existing regressions; original DLLs / Unity Mono.");
            System.Console.WriteLine("No Unity scene, world, socket or plugin Awake executed. Scoped Harmony boundaries supply only ZDO identity initialization and the terrain platform path.");
            return 0;
        }
        catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckReferences(string plugin, string managed)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.RemoveSearchDirectory(".");
        resolver.RemoveSearchDirectory("bin");
        resolver.AddSearchDirectory(managed);
        resolver.AddSearchDirectory(Path.GetDirectoryName(plugin));
        using var module = ModuleDefinition.ReadModule(plugin, new ReaderParameters { AssemblyResolver = resolver });
        int references = 0;
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (!(instruction.Operand is MemberReference member)) continue;
            string scope = (member is TypeReference tr ? tr.Scope?.Name : member.DeclaringType?.Scope?.Name) ?? "";
            if (!(scope.StartsWith("assembly_") || scope.StartsWith("Unity") || scope == "Splatform" || scope == "gui_framework")) continue;
            references++;
            if (member is FieldReference field)
            {
                var target = field.Resolve();
                Check(target != null && target.IsPublic, "Missing/nonpublic direct field: " + member);
                Check(!target.IsLiteral || instruction.OpCode.Code != Mono.Cecil.Cil.Code.Ldsfld, "Literal read as storage: " + member);
            }
            else if (member is MethodReference call)
            {
                var target = call.Resolve();
                Check(target != null && (target.IsPublic || target.IsFamily), "Missing/nonpublic direct method: " + member);
            }
            else if (member is TypeReference t) Check(t.Resolve() != null, "Missing type: " + member);
        }
        System.Console.WriteLine("Resolved " + references + " game/Unity IL references including merged ServerSync.");
    }

    private static void CheckPatches(Assembly mod)
    {
        int targets = 0;
        foreach (Type type in mod.GetTypes())
        {
            var attributes = type.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().ToArray();
            if (attributes.Length == 0) continue;
            var methods = type.GetMethods(Static);
            var individual = methods.Where(m => m.IsDefined(typeof(HarmonyPatch), false)).ToArray();
            if (individual.Length != 0)
            {
                foreach (var patch in individual) ResolvePatch(patch.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>(), new[] { patch });
            }
            else ResolvePatch(attributes, methods.Where(m => m.Name == "Prefix" || m.Name == "Postfix" || m.Name == "Finalizer").ToArray());
        }
        System.Console.WriteLine("Checked " + targets + " Harmony targets and injected parameter contracts.");

        void ResolvePatch(IEnumerable<HarmonyPatch> attributes, MethodInfo[] patches)
        {
            var info = new HarmonyMethod();
            foreach (var attribute in attributes) attribute.info.CopyTo(info);
            MethodInfo original = AccessTools.DeclaredMethod(info.declaringType, info.methodName, info.argumentTypes);
            Check(original != null, "Missing Harmony target: " + info.declaringType + "." + info.methodName);
            targets++;
            foreach (var patch in patches)
            foreach (var parameter in patch.GetParameters())
            {
                string name = parameter.Name;
                Type p = Element(parameter.ParameterType);
                if (name == "__instance") Check(p.IsAssignableFrom(original.DeclaringType), "Instance: " + patch);
                else if (name == "__result") Check(p.IsAssignableFrom(original.ReturnType), "Result: " + patch);
                else if (name == "__state")
                {
                    var prefix = patches.FirstOrDefault(m => m.Name == "Prefix");
                    var state = prefix?.GetParameters().FirstOrDefault(a => a.Name == "__state");
                    Check(state == null || Element(state.ParameterType) == p, "State pair: " + patch);
                }
                else if (name.StartsWith("___"))
                {
                    var field = AccessTools.Field(original.DeclaringType, name.Substring(3));
                    Check(field != null && p.IsAssignableFrom(field.FieldType), "Injected field: " + patch + " " + name);
                }
                else if (!name.StartsWith("__"))
                {
                    var argument = original.GetParameters().FirstOrDefault(a => a.Name == name);
                    Check(argument != null && p.IsAssignableFrom(Element(argument.ParameterType)), "Argument: " + patch + " " + name);
                }
            }
        }
    }

    private static Type Element(Type type) => type.IsByRef ? type.GetElementType() : type;

    private static void CheckSyncedValue(Assembly mod)
    {
        // Accept scheduled actions without starting Unity or installing detours.
        Type helperType = typeof(BepInEx.ThreadingHelper);
        object helper = FormatterServices.GetUninitializedObject(helperType);
        AccessTools.Field(helperType, "_invokeLock").SetValue(helper, new object());
        AccessTools.Field(helperType, "<Instance>k__BackingField").SetValue(null, helper);
        Type syncType = mod.GetType("ServerSync.ConfigSync", true);
        object sync = Activator.CreateInstance(syncType, "ZoneSavior.offline-compatibility-test");
        Type valueType = mod.GetType("ServerSync.CustomSyncedValue`1", true).MakeGenericType(typeof(string));
        object value = Activator.CreateInstance(valueType, sync, "zone_rules_yaml", "", 0);
        valueType.GetProperty("Value").SetValue(value, "test rules");
        Check((string)valueType.GetProperty("Value").GetValue(value) == "test rules", "Merged ServerSync custom value initialization/update");
    }

    private static void CheckZdoAndZones(Assembly mod)
    {
        // Managed state only: no game constructors or live singleton instances.
        var manager = (ZDOMan)FormatterServices.GetUninitializedObject(typeof(ZDOMan));
        AccessTools.Field(typeof(ZDOMan), "s_instance").SetValue(null, manager);
        AccessTools.Field(typeof(ZDOMan), "m_dirtyChunks").SetValue(manager,
            new[] { new HashSet<ZoneSystem.ChunkIndex>(), new HashSet<ZoneSystem.ChunkIndex>(), new HashSet<ZoneSystem.ChunkIndex>() });
        AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, FormatterServices.GetUninitializedObject(typeof(ZNet)));
        AccessTools.Field(typeof(ZNet), "m_isServer").SetValue(null, true);
        ZDOExtraData.Init();
        var source = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); source.m_uid = new ZDOID(100L, 1);
        source.Init(); source.Persistent = false; source.Distant = true; source.Type = ZDO.ObjectType.Solid;
        source.Set(11, "contents"); source.Set(12, 3.5f); source.Set(13, -17); source.Set(14, 1234567890123L);
        source.Set(15, new Vector3(1, 2, 3)); source.Set(16, new Quaternion(0, 0, 0, 1));
        byte[] buffer = { 1, 2, 3, 255 }; source.Set(17, buffer);
        source.Set(18, true); source.Set(19, false); source.Set(20, 0); source.Set(20, "same hash, different type");
        source.SetOwner(700L);
        source.SetConnection(ZDOExtraData.ConnectionType.Spawned, new ZDOID(700L, 50));
        uint sourceRevision = source.DataRevision;
        ushort sourceOwnerRevision = source.OwnerRevision;
        Type dataType = mod.GetType("ZoneSavior.ZoneBundleZdoData", true);
        object saved = Activator.CreateInstance(dataType, source);
        string payload = (string)dataType.GetMethod("GetBase64").Invoke(saved, null);
        Check(source.DataRevision == sourceRevision && source.OwnerRevision == sourceOwnerRevision && source.GetOwner() == 700L,
            "Reading live indexed values does not mutate source revision or ownership");
        var savedInts = (Dictionary<int, int>)dataType.GetProperty("Ints").GetValue(saved);
        Check(savedInts.ContainsKey(19) && savedInts[19] == 0 && savedInts.ContainsKey(20), "Present zero/false entries remain present");
        buffer[0] = 99; source.Set(11, "changed"); source.Set(12, 9.5f); source.Set(13, 999);
        source.Set(14, -1L); source.Set(15, new Vector3(4, 5, 6)); source.Set(16, new Quaternion(1, 0, 0, 0));
        source.Set(17, new byte[] { 9 });
        for (int i = -1; i >= -65; i--) source.Set(i, "new key " + i);
        ZDOExtraData.RemoveString(source.m_uid, 20);
        Check(payload == (string)dataType.GetMethod("GetBase64").Invoke(saved, null), "Snapshot does not alias live maps/buffers");
        object decoded = Activator.CreateInstance(dataType, payload);
        Check(payload == (string)dataType.GetMethod("GetBase64").Invoke(decoded, null), "Seven-map wire round trip");
        var restored = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); restored.m_uid = new ZDOID(100L, 2); restored.Init();
        restored.SetOwner(800L);
        ushort restoredOwnerRevision = restored.OwnerRevision;
        uint restoredRevision = restored.DataRevision;
        dataType.GetMethod("ApplyTo").Invoke(decoded, new object[] { restored });
        Check(restored.GetString(11) == "contents" && restored.GetFloat(12) == 3.5f, "Strings/floats restored");
        Check(restored.GetInt(13) == -17 && restored.GetLong(14) == 1234567890123L, "Integers/longs restored");
        Check(restored.GetVec3(15, Vector3.zero).Equals(new Vector3(1, 2, 3)) && restored.GetQuaternion(16, default).w == 1, "Vectors/quaternions restored");
        Check(restored.GetByteArray(17)[0] == 1 && restored.GetByteArray(17)[3] == 255, "Inventory/TCData byte buffers preserved");
        Check(restored.GetBool(18) && !restored.GetBool(19) && restored.GetInt(20) == 0 && restored.GetString(20) == "same hash, different type", "Boolean and cross-type keys preserved");
        Check(!restored.Persistent && restored.Distant && restored.Type == source.Type, "ZDO metadata preserved");
        Check(restored.m_uid != source.m_uid, "Restored identity is independent");
        Check(restored.GetOwner() == 800L && restored.OwnerRevision == restoredOwnerRevision,
            "Restore retains destination ownership instead of copying the archived owner");
        Check(restored.DataRevision > restoredRevision, "Restore uses native Set change/revision handling");
        Check(restored.GetConnectionZDOID(ZDOExtraData.ConnectionType.Spawned).IsNone(),
            "Old spawned connection IDs are not copied into restored objects");
        restored.GetByteArray(17)[0] = 77;
        Check(payload == (string)dataType.GetMethod("GetBase64").Invoke(decoded, null),
            "Restored inventory buffers cannot mutate the decoded archive");

        // This v1 payload is written independently of ZoneBundleZdoData: one negative string
        // key, an explicit zero-valued integer key, and five empty dictionaries.
        var legacy = new ZPackage();
        legacy.Write(1); legacy.Write(false); legacy.Write(false); legacy.Write((int)ZDO.ObjectType.Default);
        legacy.Write(1); legacy.Write(-7); legacy.Write("legacy contents");
        legacy.Write(0); legacy.Write(1); legacy.Write(0); legacy.Write(0);
        legacy.Write(0); legacy.Write(0); legacy.Write(0); legacy.Write(0);
        string legacyPayload = "zs-zdo-v1:" + legacy.GetBase64();
        object legacyData = Activator.CreateInstance(dataType, legacyPayload);
        Check(legacyPayload == (string)dataType.GetMethod("GetBase64").Invoke(legacyData, null),
            "Existing v1 payload format retains negative keys, explicit zero entries and empty maps");
        var empty = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
        empty.m_uid = new ZDOID(100L, 3); empty.Init(); empty.Persistent = true;
        ZDOExtraData.Reserve(empty.m_uid, ZDOExtraData.Type.String, 64);
        ZDOExtraData.Reserve(empty.m_uid, ZDOExtraData.Type.ByteArray, 64);
        object flagsOnly = Activator.CreateInstance(dataType, empty);
        foreach (string property in new[] { "Strings", "Floats", "Ints", "Longs", "Vecs", "Quats", "ByteArrays" })
            Check(((System.Collections.IDictionary)dataType.GetProperty(property).GetValue(flagsOnly)).Count == 0,
                "Empty indexed view produces no capacity/default entries: " + property);
        var flagsTarget = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); flagsTarget.Init();
        dataType.GetMethod("ApplyTo").Invoke(flagsOnly, new object[] { flagsTarget });
        Check(flagsTarget.Persistent, "Persistent metadata restored (no native dirty-sector callback)");

        Type zones = mod.GetType("ZoneSavior.ZoneSaviorZones", true);
        MethodInfo find = zones.GetMethod("FindObjects", Static);
        var sectors = new List<ZDO>[512 * 512];
        var portals = new Dictionary<ZoneSystem.SectorIndex, List<ZDO>>();
        AccessTools.Field(typeof(ZDOMan), "m_objectsBySector").SetValue(manager, sectors);
        AccessTools.Field(typeof(ZDOMan), "m_portalObjects").SetValue(manager, portals);
        var result = new List<ZDO>();
        bool rejected = false;
        try { find.Invoke(null, new object[] { new Vector2i(65536, 0), result }); }
        catch (TargetInvocationException e) { rejected = e.InnerException is ArgumentOutOfRangeException; }
        Check(rejected, "Reject short overflow instead of wrapping destructive requests");
        Type commands = mod.GetType("ZoneSavior.ZoneBundleCommands", true);
        rejected = false;
        try { commands.GetMethod("NormalizeZones", Static).Invoke(null, new object[] { new[] { new Vector2i(0, 0), new Vector2i(32768, 0) } }); }
        catch (TargetInvocationException e) { rejected = e.InnerException is ArgumentOutOfRangeException; }
        Check(rejected, "Entire save/reset zone list validated before execution");
        System.Console.WriteLine("PASS ZDO snapshot/restore and coordinate range guard (native geometry deferred).");
    }

    private static void CheckResetSnapshots(Assembly mod)
    {
        Type scanner = mod.GetType("ZoneSavior.AutoArchiveScanner", true);
        MethodInfo matches = scanner.GetMethod("ResetSnapshotMatches", Static);
        ZDOID first = new ZDOID(600L, 1), second = new ZDOID(600L, 2), added = new ZDOID(600L, 3);
        var saved = new Dictionary<ZDOID, string> { [first] = "piece+creator+inventory:before", [second] = "terrain:before" };
        bool Match(Dictionary<ZDOID, string> current, params ZDOID[] destroyed)
        {
            object[] args = { saved, current, new HashSet<ZDOID>(destroyed), null };
            bool accepted = (bool)matches.Invoke(null, args);
            Check(accepted || !string.IsNullOrWhiteSpace((string)args[3]), "Changed reset snapshot supplies a refusal reason");
            return accepted;
        }
        var equal = new Dictionary<ZDOID, string> { [second] = "terrain:before", [first] = "piece+creator+inventory:before" };
        Check(Match(equal), "Unchanged snapshot permits initial reset independent of dictionary insertion order");
        var more = new Dictionary<ZDOID, string>(equal) { [added] = "new construction" };
        Check(!Match(more), "Construction after save blocks destruction");
        more.Remove(second);
        Check(!Match(more, second), "New construction during a prior reset batch still blocks further destruction");
        var less = new Dictionary<ZDOID, string> { [first] = "piece+creator+inventory:before" };
        Check(!Match(less), "A moved animal or externally destroyed chest blocks reset when its absence is unexplained");
        Check(!Match(less, added), "A different self-destroyed ID does not excuse a missing saved object");
        Check(Match(less, second), "An explicitly recorded self-destroyed object may be absent in the next batch");
        Check(!Match(new Dictionary<ZDOID, string>()), "A completely vanished snapshot cannot start a fresh reset");
        Check(!Match(new Dictionary<ZDOID, string>(), first), "An unexplained disappearance during reset blocks completion even after another legitimate deletion");
        Check(Match(new Dictionary<ZDOID, string>(), first, second), "Only recorded self-destruction of every captured object permits an empty completed batch");
        var changed = new Dictionary<ZDOID, string>(equal) { [first] = "piece+creator+inventory:changed" };
        Check(!Match(changed), "Inventory, ownership or other captured content changes block initial reset");
        changed.Remove(second);
        Check(!Match(changed, second), "Prior self-destruction cannot excuse changes to the remaining objects");
        Check(saved.Count == 2 && saved[first] == "piece+creator+inventory:before", "Snapshot comparisons never mutate the archive baseline");
        System.Console.WriteLine("PASS reset snapshot guard: additions, payload changes, moved/external-deleted objects and explicit self-destruction tracking.");
    }
}

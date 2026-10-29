// Adapted from FreshWorld's real-DLL compatibility regression. Tests the final ZoneSavior
// DLL and external plugins together, without patching selection or mapping implementation.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Chunk = ZoneSystem.ChunkIndex;
using ChunkObjects = System.Tuple<ZoneSystem.ChunkIndex, System.Collections.Generic.List<ZDO>>;
using Fixture = ZoneSavior.Tests.EmptyChunkSelectionChecks.Fixture;
using static ZoneSavior.Tests.ProbeContext;

namespace ZoneSavior.Tests;

internal static class EmptyChunkHarmonyChecks
{
    private static int admitted;
    private static readonly Chunk Target = new(0x1010);

    internal static void Run()
    {
        Test("real Harmony discovery and original private-field injection replace native omission", () =>
        {
            foreach (var name in new[] { "m_chunkSaveMapping", "m_dirtyChunks", "m_objectsBySector", "m_saveData" })
                Require(typeof(ZDOMan).GetField(name, All)!.IsPrivate, "Expected original private field: " + name);
            var f = Saved(); Equal(0, Select(f).Count);
            InstallZone(); var selected = Select(f); Equal(1, selected.Count); Equal(Target, selected[0].Item1);
            Equal(0, selected[0].Item2.Count); Equal(5, f.Counter("m_dirtyFiles"));
        });
        Test("actual postfix leaves remote clients and absent hosts unchanged", () =>
        {
            InstallZone(); var f = Saved();
            typeof(ZNet).GetField("m_isServer", All)!.SetValue(null, false); Equal(0, Select(f).Count);
            typeof(ZNet).GetField("m_instance", All)!.SetValue(null, null); Equal(0, Select(f).Count);
        });
        Test("late Upgrade World registration changes owner at the next save boundary", () =>
        {
            InstallZone(); Equal(1, Select(Saved()).Count); InstallUpgrade();
            Equal("UpgradeWorld", Check()); var f = Saved(); Equal(0, Select(f).Count); Equal(4, f.Counter("m_dirtyFiles"));
        });
        foreach (var zoneFirst in new[] { true, false })
        {
            var first = zoneFirst;
            Test("FreshWorld + ZoneSavior avoid duplicate replacements; ZoneSavior first=" + first, () =>
            {
                if (first) { InstallZone(); InstallFresh(); } else { InstallFresh(); InstallZone(); }
                var f = Saved(); var selected = Select(f);
                Equal(1, selected.Count); Equal(5, f.Counter("m_numFiles")); Equal(5, f.Counter("m_dirtyFiles"));
                RoundTrip(1, 1);
            });
        }
        foreach (var order in new[] { "ZFU", "ZUF", "FZU", "FUZ", "UZF", "UFZ" })
        {
            var chosen = order;
            Test("three-way registration " + chosen + " delegates to Upgrade World exactly once", () =>
            {
                foreach (var item in chosen)
                    if (item == 'Z') InstallZone(); else if (item == 'F') InstallFresh(); else InstallUpgrade();
                Equal("UpgradeWorld", Check()); Equal("UpgradeWorld", Call(FreshCompatibility, null, "Check")!.ToString());
                var f = Saved(); Equal(0, Select(f).Count); Equal(4, f.Counter("m_dirtyFiles"));
                RoundTrip(1, 0);
            });
        }
        foreach (var mask in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var partial = mask;
            Test("partial Upgrade World set " + partial + " stops mutation without vetoing world save", () =>
            {
                InstallZone(); InstallUpgrade(partial);
                Equal("Unsupported", Check());
                Require(Compatibility.GetProperty("DisabledReason", All)!.GetValue(null) != null, "Reason was not latched.");
                var f = Saved(); Equal(0, Select(f).Count); Equal(4, f.Counter("m_dirtyFiles"));
                ExpectMutationDenied(); ProbeSaveAdmission();
                InstallUpgrade(); Equal("Unsupported", Check()); ExpectMutationDenied();
            });
        }
        Test("runtime helper detour is rechecked after the original contract was cached", () =>
        {
            InstallUpgrade(); Equal("UpgradeWorld", Check());
            var helper = Method(Upgrade.GetType("UpgradeWorld.ChunkSaveFix", true)!, "IsEmpty");
            BoundaryHarmony.Patch(helper, prefix: new HarmonyMethod(typeof(EmptyChunkHarmonyChecks), nameof(Noop)));
            try { Equal("Unsupported", Check()); ExpectMutationDenied(); }
            finally { BoundaryHarmony.Unpatch(helper, Method(typeof(EmptyChunkHarmonyChecks), nameof(Noop))); }
        });
        Test("unrelated patch does not impersonate Upgrade World", () =>
        {
            var target = Method(typeof(ChunkSaveMapping), "Clone");
            BoundaryHarmony.Patch(target, postfix: new HarmonyMethod(typeof(EmptyChunkHarmonyChecks), nameof(Noop)));
            try { Equal("ZoneSavior", Check()); }
            finally { BoundaryHarmony.Unpatch(target, Method(typeof(EmptyChunkHarmonyChecks), nameof(Noop))); }
        });
        Test("expected Upgrade World owner with an unknown hook is rejected", () =>
        {
            UpgradeHarmony.Patch(Method(typeof(ChunkSaveMapping), "Clone"), postfix: new HarmonyMethod(typeof(EmptyChunkHarmonyChecks), nameof(Noop)));
            Equal("Unsupported", Check());
        });
        Test("expected implementation installed by another owner is rejected", () =>
        {
            var hook = Upgrade.GetType("UpgradeWorld.DropEmptiedChunks", true)!;
            BoundaryHarmony.CreateClassProcessor(hook).Patch();
            try { Equal("Unsupported", Check()); }
            finally { BoundaryHarmony.Unpatch(Method(typeof(ChunkSaveMapping), "Clone"), Method(hook, "Postfix")); }
        });
        Test("unrelated version, MVID and metadata changes retain the narrow contract", () => Variant(module =>
        {
            module.Mvid = Guid.NewGuid(); module.Assembly.Name.Version = new System.Version(77, 8, 9, 0);
            module.Types.Insert(1, new Mono.Cecil.TypeDefinition("Unrelated", "NewFeature", Mono.Cecil.TypeAttributes.Public, module.TypeSystem.Object));
        }, true));
        Test("equivalent no-ops and long branches retain the contract", () => Variant(module =>
        {
            var body = Helper(module).Body;
            body.Instructions.Insert(0, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Nop));
            foreach (var instruction in body.Instructions)
                if (instruction.OpCode == Mono.Cecil.Cil.OpCodes.Br_S) instruction.OpCode = Mono.Cecil.Cil.OpCodes.Br;
        }, true));
        Test("changed helper logic is rejected without relying on a DLL version", () => Variant(module =>
            Helper(module).Body.Instructions.First(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4_8).OpCode = Mono.Cecil.Cil.OpCodes.Ldc_I4_7, false));
        Test("changed shared-field lifetime is rejected", () => Variant(module =>
            module.Types.Single(t => t.FullName == "UpgradeWorld.ChunkSaveFix").Fields.Single(f => f.Name == "Dropped").IsInitOnly = false, false));
        Test("thread-local shared state is rejected", () => Variant(module =>
        {
            var attribute = new Mono.Cecil.CustomAttribute(module.ImportReference(typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes)!));
            module.Types.Single(t => t.FullName == "UpgradeWorld.ChunkSaveFix").Fields.Single().CustomAttributes.Add(attribute);
        }, false));
        Test("changed Harmony parameter mapping is rejected", () => Variant(module =>
            module.Types.Single(t => t.FullName == "UpgradeWorld.FindEmptiedChunks").Methods.Single(m => m.Name == "Prefix").Parameters[0].Name = "__result", false));
        Test("changed exception cleanup is rejected", () => Variant(module =>
            module.Types.Single(t => t.FullName == "UpgradeWorld.DropEmptiedChunks").Methods.Single(m => m.Name == "Postfix").Body.ExceptionHandlers.Clear(), false));
        foreach (var revision in new uint[] { 1, 2, 7 })
        foreach (var strategy in new[] { "ZoneSavior", "UpgradeWorld", "Both" })
        {
            var r = revision; var s = strategy;
            Test(s + " revision " + r + " survives two native saves, mapping reload and backup", () =>
            {
                if (s != "UpgradeWorld") InstallZone();
                if (s != "ZoneSavior") InstallUpgrade();
                RoundTrip(r, s == "ZoneSavior" ? 1 : 0);
            });
        }
        Test("forced old mixed strategy reproduces revision-one missing-file failure", ReproduceConflict);
    }

    private static Mono.Cecil.MethodDefinition Helper(Mono.Cecil.ModuleDefinition module) =>
        module.Types.Single(t => t.FullName == "UpgradeWorld.ChunkSaveFix").Methods.Single(m => m.Name == "IsEmpty");
    private static void Variant(Action<Mono.Cecil.ModuleDefinition> mutate, bool expected)
    {
        using var definition = Mono.Cecil.AssemblyDefinition.ReadAssembly(UpgradePath);
        mutate(definition.MainModule);
        using var output = new MemoryStream(); definition.Write(output);
        var args = new object?[] { Assembly.Load(output.ToArray()), null };
        try { Equal(expected, Call(Contract, null, "Recognizes", args)); }
        catch (TargetInvocationException error) when (!expected && error.InnerException is InvalidOperationException) { return; }
    }
    private static void ExpectMutationDenied()
    {
        try { Call(Compatibility, null, "RequireSafeMutation"); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { return; }
        throw new Exception("Unrecognized save contract did not stop mutation.");
    }
    private static void ProbeSaveAdmission()
    {
        var method = typeof(ZNet).GetMethod("SaveWorld", All, null, new[] { typeof(bool) }, null)!;
        admitted = 0;
        BoundaryHarmony.Patch(method, prefix: new HarmonyMethod(typeof(EmptyChunkHarmonyChecks), nameof(StopBeforeUnity)) { priority = Priority.Last });
        try { method.Invoke(ZNet.instance, new object[] { false }); Equal(1, admitted); }
        finally { BoundaryHarmony.Unpatch(method, Method(typeof(EmptyChunkHarmonyChecks), nameof(StopBeforeUnity))); }
    }
    private static bool StopBeforeUnity(bool __runOriginal) { if (__runOriginal) admitted++; return false; }
    private static void Noop() { }
    private static Fixture Saved(uint revision = 1)
    {
        var f = new Fixture(); f.Saved(Target); f.MarkDirty(Target); f.Mapping.Get(Target).m_version = revision; return f;
    }
    private static List<ChunkObjects> Select(Fixture f) => (List<ChunkObjects>)Call(typeof(ZDOMan), f.Manager, "GetSaveClonePerChunk")!;
    private static ChunkSaveMapping Current(Fixture f) => (ChunkSaveMapping)Get(f.Manager, "m_chunkSaveMapping");
    private static void Save(Fixture f, string directory, List<ChunkObjects> selected)
    {
        Set(f.SaveData, "m_objectsByChunk", selected);
        Equal(true, f.Manager.SaveChunks(directory, FileHelpers.FileSource.Local));
        f.Manager.DeleteOldChunks(directory, FileHelpers.FileSource.Local);
    }
    private static void RoundTrip(uint revision, int expectedChunks)
    {
        WithDirectory(directory =>
        {
            var f = Saved(revision);
            File.WriteAllBytes(Path.Combine(directory, f.Mapping.GetChunkFilename(Target)), new byte[] { 42, 0, 123, 0, 0, 0 });
            for (var pass = 0; pass < 2; pass++)
            {
                Save(f, directory, Select(f)); Verify(directory, expectedChunks);
                Equal(expectedChunks, Current(f).GetNumChunks());
            }
            var backup = Path.Combine(directory, "backup"); Directory.CreateDirectory(backup);
            foreach (var path in Directory.GetFiles(directory)) File.Copy(path, Path.Combine(backup, Path.GetFileName(path)));
            Verify(backup, expectedChunks);
        });
    }
    private static void ReproduceConflict()
    {
        InstallUpgrade();
        WithDirectory(directory =>
        {
            var f = Saved(); var selected = Select(f);
            // Deliberately bypass arbitration to prove the compatibility guard is necessary.
            selected.Add(Tuple.Create(Target, new List<ZDO>())); Save(f, directory, selected);
            Equal(1, Current(f).GetNumChunks());
            Require(!File.Exists(Path.Combine(directory, Current(f).GetChunkFilename(Target))), "Expected filename collision was not reproduced.");
        });
    }
    private static void Verify(string directory, int count)
    {
        var loaded = new ChunkSaveMapping(); loaded.Load(directory, FileHelpers.FileSource.Local);
        Equal(count, loaded.GetNumChunks());
        foreach (var entry in loaded.Chunks)
        {
            var file = Path.Combine(directory, loaded.GetChunkFilename(entry.Key));
            Require(File.Exists(file), "Saved mapping references a missing file: " + file);
            using var reader = new BinaryReader(File.OpenRead(file));
            Equal((short)42, reader.ReadInt16()); Equal(0, reader.ReadInt32()); Equal(6L, reader.BaseStream.Length);
        }
    }
    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ZoneSavior-empty-chunk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory + Path.DirectorySeparatorChar); }
        finally
        {
            var full = Path.GetFullPath(directory);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Require(full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("ZoneSavior-empty-chunk-test-", StringComparison.Ordinal), "Unsafe fixture cleanup target.");
            Directory.Delete(full, true);
        }
    }
}

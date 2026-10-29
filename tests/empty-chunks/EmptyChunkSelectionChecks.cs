// Adapted from FreshWorld.Save.Tests/Program.cs. Uses original game types and the final
// ZoneSavior DLL; this fixture never initializes Unity or invokes a plugin Awake.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Chunk = ZoneSystem.ChunkIndex;
using ChunkObjects = System.Tuple<ZoneSystem.ChunkIndex, System.Collections.Generic.List<ZDO>>;
namespace ZoneSavior.Tests;
internal static class EmptyChunkSelectionChecks
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run()
    {
        var tests = new List<(string Name, Action Test)>();
        for (byte size = 0; size <= 3; size++)
        {
            var selected = size;
            tests.Add(($"native omission is repaired for {8 << size} x {8 << size} zone chunks", () => EmptyChunk(selected)));
            tests.Add(($"a remaining object at the far edge of size {size} preserves the native save", () => OccupiedChunk(selected)));
        }
        tests.AddRange(new (string, Action)[]
        {
            ("unchanged and already-empty saved chunks are not rewritten", UnchangedChunks),
            ("new unsaved regions do not acquire empty chunk files", UnsavedChunk),
            ("native or competing empty replacements are not duplicated", ExistingReplacement),
            ("child and parent replacements prevent overlapping save entries", ResizedChunks),
            ("retired mappings and the portal chunk are untouched", SpecialChunks),
            ("transient or pending-delete sector records prevent empty replacements", TransientRecord),
            ("neighboring native entries and their object snapshots are preserved", NeighborPreserved),
            ("failed-save retries retain dirty state and live chunk metadata", Retry),
            ("world edge buckets use native sector mapping", WorldEdge),
            ("absent hosts and remote clients cannot modify the save list", HostOnly)
        });
        var failures = 0;
        foreach (var (name, test) in tests)
        {
            try { test(); System.Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; System.Console.Error.WriteLine("FAIL " + name + ": " + error); }
        }
        System.Console.WriteLine($"{tests.Count - failures}/{tests.Count} original-game save regression checks passed (isolated managed execution, not Unity gameplay).");
        return failures == 0 ? 0 : 1;
    }

    private static void EmptyChunk(byte size)
    {
        var f = new Fixture();
        var chunk = new Chunk(0x1010, size);
        f.Saved(chunk);
        f.MarkDirty(chunk);
        var native = f.SelectNativeChunks();
        Equal(0, native.Count); // Reproduce the original omission before applying production code.
        f.Patch(native);
        Equal(1, native.Count); Equal(chunk, native[0].Item1); Equal(0, native[0].Item2.Count);
        Equal(5, f.Counter("m_numFiles")); Equal(5, f.Counter("m_dirtyFiles"));
        // The native mapping accepts this exact chunk as a replacement, with a new filename.
        var copy = f.Mapping.Clone();
        var oldFile = copy.GetChunkFilename(chunk);
        copy.CreateOrUpdate(native[0].Item1);
        Require(copy.GetChunkFilename(chunk) != oldFile, "Native replacement must advance the filename/version.");
        Equal(123, f.Mapping.Get(chunk).m_numZDOs); Equal(1u, f.Mapping.Get(chunk).m_version);
    }

    private static void OccupiedChunk(byte size)
    {
        var f = new Fixture(); var chunk = new Chunk(0x1010, size);
        f.Saved(chunk); f.MarkDirty(chunk); f.AddObject(chunk, persistent: true, farEdge: true);
        var native = f.SelectNativeChunks();
        Require(native.Sum(entry => entry.Item2.Count) == 1, "Native save must include the remaining object.");
        var before = native.ToArray(); f.Patch(native);
        Require(before.SequenceEqual(native), "A native nonempty replacement changed.");
        // Also reject an empty replacement if an upstream patch omits a nonempty chunk.
        var missing = new List<ChunkObjects>(); f.Patch(missing); Equal(0, missing.Count);
    }

    private static void UnchangedChunks()
    {
        var f = new Fixture(); var chunk = new Chunk(0x1010);
        f.Saved(chunk); var output = new List<ChunkObjects>(); f.Patch(output); Equal(0, output.Count);
        f.MarkDirty(chunk); f.Mapping.Get(chunk).m_numZDOs = 0; f.Patch(output); Equal(0, output.Count);
        Equal(4, f.Counter("m_dirtyFiles"));
    }

    private static void UnsavedChunk()
    {
        var f = new Fixture(); f.MarkDirty(new Chunk(0x1010));
        var output = f.SelectNativeChunks(); f.Patch(output); Equal(0, output.Count); Equal(0, f.Mapping.Chunks.Count);
    }

    private static void ExistingReplacement()
    {
        var f = new Fixture(); var chunk = new Chunk(0x1010); f.Saved(chunk); f.MarkDirty(chunk);
        var entry = Tuple.Create(chunk, new List<ZDO>()); var output = new List<ChunkObjects> { entry };
        f.Patch(output); Equal(1, output.Count); Require(ReferenceEquals(entry, output[0]), "Existing replacement changed.");
        Equal(4, f.Counter("m_numFiles"));
    }

    private static void ResizedChunks()
    {
        foreach (var expanding in new[] { false, true })
        {
            var f = new Fixture(); var small = new Chunk(0x1010); var large = new Chunk(0x1010, 3);
            var old = expanding ? small : large; var replacement = expanding ? large : small;
            f.Saved(old); f.MarkDirty(old);
            var output = new List<ChunkObjects> { Tuple.Create(replacement, new List<ZDO>()) };
            f.Patch(output); Equal(1, output.Count); Equal(replacement, output[0].Item1);
        }
    }

    private static void SpecialChunks()
    {
        var f = new Fixture(); var retired = new Chunk(0x1010, 1);
        f.Saved(retired); f.MarkDirty(retired); f.Mapping.Get(retired).m_sizeChanged = true;
        f.Saved(ZoneSystem.ChunkPortal); f.Dirty[0].Add(ZoneSystem.ChunkPortal);
        var output = new List<ChunkObjects>(); f.Patch(output); Equal(0, output.Count);
    }

    private static void TransientRecord()
    {
        var f = new Fixture(); var chunk = new Chunk(0x1010); f.Saved(chunk); f.MarkDirty(chunk);
        f.AddObject(chunk, persistent: false);
        var output = new List<ChunkObjects>(); f.Patch(output); Equal(0, output.Count);
    }

    private static void NeighborPreserved()
    {
        var f = new Fixture(); var empty = new Chunk(0x1010); var neighbor = new Chunk(0x1110);
        f.Saved(empty); f.Saved(neighbor); f.MarkDirty(empty); f.MarkDirty(neighbor);
        f.AddObject(neighbor, persistent: true);
        var output = f.SelectNativeChunks(); var existing = output.Single();
        f.Patch(output); Equal(2, output.Count);
        Require(output.Contains(existing) && existing.Item2.Count == 1, "Neighbor's native snapshot was changed.");
        Equal(0, output.Single(entry => entry.Item1.Equals(empty)).Item2.Count);
    }

    private static void Retry()
    {
        var f = new Fixture(); var chunk = new Chunk(0x1010); f.Saved(chunk); f.MarkDirty(chunk);
        var filename = f.Mapping.GetChunkFilename(chunk); var dirty = f.Dirty[0].ToArray();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            f.ResetCounters(); var output = f.SelectNativeChunks(); f.Patch(output); Equal(1, output.Count);
            Equal(filename, f.Mapping.GetChunkFilename(chunk)); Equal(123, f.Mapping.Get(chunk).m_numZDOs);
            Require(f.Dirty[0].SetEquals(dirty), "Correction consumed dirty flags before save completion.");
            f.Patch(output); Equal(1, output.Count); Equal(5, f.Counter("m_dirtyFiles"));
            f.BeginSave(); f.MarkDirty(chunk); f.EndSave(success: false);
            Require(f.Dirty[0].SetEquals(dirty) && f.Dirty[1].Count == 0,
                "Native failed-save handling must retain dirty chunks for a retry.");
        }
        f.Mapping.Get(chunk).m_numZDOs = 0; // State after a successful native empty replacement.
        f.BeginSave(); f.EndSave(success: true); Equal(0, f.Dirty[0].Count);
        f.MarkDirty(chunk);
        var after = new List<ChunkObjects>(); f.Patch(after); Equal(0, after.Count);
    }

    private static void WorldEdge()
    {
        foreach (var chunk in new[] { ZoneSystem.ChunkZero, new Chunk(0x3f3f), new Chunk(0x3838, 3) })
        {
            var f = new Fixture(); f.Saved(chunk); f.MarkDirty(chunk);
            var output = new List<ChunkObjects>(); f.Patch(output); Equal(1, output.Count);
            f.AddObject(chunk, persistent: true, farEdge: true);
            output.Clear(); f.Patch(output); Equal(0, output.Count);
        }
    }

    private static void HostOnly()
    {
        var f = new Fixture(); var chunk = new Chunk(0x1010); f.Saved(chunk); f.MarkDirty(chunk);
        var output = new List<ChunkObjects>();
        typeof(ZNet).GetField("m_isServer", StaticFields)!.SetValue(null, false);
        f.Patch(output); Equal(0, output.Count);
        typeof(ZNet).GetField("m_instance", StaticFields)!.SetValue(null, null);
        f.Patch(output); Equal(0, output.Count);
    }

    internal sealed class Fixture
    {
        internal readonly ZDOMan Manager = (ZDOMan)FormatterServices.GetUninitializedObject(typeof(ZDOMan));
        internal readonly ChunkSaveMapping Mapping = new();
        internal readonly HashSet<Chunk>[] Dirty = [new(), new()];
        internal readonly List<ZDO>[] Sectors = new List<ZDO>[512 * 512];
        internal readonly object SaveData = Activator.CreateInstance(typeof(ZDOMan).GetNestedType("SaveData", BindingFlags.NonPublic)!, true)!;

        internal Fixture()
        {
            typeof(ZNet).GetField("m_instance", StaticFields)!.SetValue(null, FormatterServices.GetUninitializedObject(typeof(ZNet)));
            typeof(ZNet).GetField("m_isServer", StaticFields)!.SetValue(null, true);
            var game = FormatterServices.GetUninitializedObject(typeof(Game));
            typeof(Game).GetProperty("instance")!.SetValue(null, game);
            typeof(Game).GetProperty("PortalPrefabHash")!.SetValue(game, new List<int>());
            Set("m_chunkSaveMapping", Mapping); Set("m_dirtyChunks", Dirty); Set("m_objectsBySector", Sectors);
            Set("m_dirtyPortalObjects", new bool[2]);
            Set("m_portalObjects", new Dictionary<ZoneSystem.SectorIndex, List<ZDO>>());
            typeof(ZNet).GetField("m_zdoMan", InstanceFields)!.SetValue(ZNet.instance, Manager);
            Set("m_saveData", SaveData); ResetCounters();
        }

        internal void Saved(Chunk chunk)
        {
            Mapping.CreateOrUpdate(chunk); Mapping.Get(chunk).m_numZDOs = 123;
        }
        internal void MarkDirty(Chunk chunk)
        {
            var (x, y) = ZoneSystem.GetZoneFromChunk(chunk);
            Invoke("SetDirtyChunks", ZoneSystem.SectorToIndex(x, y));
        }
        internal void AddObject(Chunk chunk, bool persistent, bool farEdge = false)
        {
            var (x, y) = ZoneSystem.GetZoneFromChunk(chunk);
            if (farEdge) { x += (8 << chunk.m_chunkSize) - 1; y += (8 << chunk.m_chunkSize) - 1; }
            var index = ZoneSystem.SectorToIndex(x, y).Sector;
            var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); zdo.Persistent = persistent;
            (Sectors[index] ??= new()).Add(zdo);
            Invoke("SetDirtyChunks", new ZoneSystem.SectorIndex(index));
        }
        internal void Patch(List<ChunkObjects> result) => ProbeContext.ZonePatch.GetMethod("Postfix", ProbeContext.All).Invoke(null, new object[] { result, Mapping, Dirty, Sectors, SaveData });
        internal void BeginSave() => Invoke("BeginSave");
        internal void EndSave(bool success) { Manager.EndSave(success); Invoke("UpdateSaveState"); }
        internal int Counter(string name) => (int)SaveData.GetType().GetField(name)!.GetValue(SaveData)!;
        internal void ResetCounters()
        {
            SaveData.GetType().GetField("m_numFiles")!.SetValue(SaveData, 4);
            SaveData.GetType().GetField("m_dirtyFiles")!.SetValue(SaveData, 4);
        }
        internal List<ChunkObjects> SelectNativeChunks()
        {
            // Run original selection helpers. The enclosing method's final Unity log requires
            // an actual game process; compute sector counts here without copying its selection logic.
            var counts = new[] { new int[4096], new int[1024], new int[256], new int[64] };
            for (uint index = 0; index < Sectors.Length; index++)
            {
                if (Sectors[index] == null) continue;
                var chunk = ZoneSystem.GetZonesChunk(new ZoneSystem.SectorIndex(index));
                counts[0][(chunk.Chunk & 0xff) + (chunk.Chunk >> 8) * 64] += Sectors[index].Count;
            }
            for (byte size = 1; size <= 3; size++)
                Invoke("DecideChunkSize", (uint)(64 >> (size - 1)), size, counts[size - 1], counts[size]);
            var output = new List<ChunkObjects>();
            for (byte size = 0; size <= 3; size++) Invoke("AddObjectsPerChunk", 64 >> size, size, counts[size], output);
            return output;
        }
        internal void Set(string name, object value) => typeof(ZDOMan).GetField(name, InstanceFields)!.SetValue(Manager, value);
        internal void Invoke(string name, params object[] arguments) =>
            typeof(ZDOMan).GetMethod(name, InstanceFields)!.Invoke(Manager, arguments);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}

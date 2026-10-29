using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ZoneSavior;

/// <summary>Persist deleted chunks through the game's normal save transaction.</summary>
[HarmonyPatch(typeof(ZDOMan), "GetSaveClonePerChunk", new Type[] { })]
internal static class ZoneEmptyChunkSavePatch
{
    private static FieldInfo? FileCount;
    private static FieldInfo? DirtyFileCount;

    // Bind inside Check's failure boundary, not a type initializer that could escape into a save.
    internal static void ValidateContract()
    {
        if (FileCount != null && DirtyFileCount != null) return;
        var fileCount = FindCounter("m_numFiles");
        var dirtyFileCount = FindCounter("m_dirtyFiles");
        FileCount = fileCount;
        DirtyFileCount = dirtyFileCount;
    }

    private static FieldInfo FindCounter(string name)
    {
        var type = typeof(ZDOMan).GetNestedType("SaveData", BindingFlags.NonPublic)
            ?? throw new TypeLoadException("ZDOMan.SaveData");
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null || field.FieldType != typeof(int) || field.IsInitOnly)
            throw new MissingFieldException("ZDOMan.SaveData", name);
        return field;
    }

    // GetSaveClonePerChunk runs on the main thread, before BeginSave and the save worker.
    // Harmony supplies private fields without compiling against publicized game assemblies.
    [HarmonyPostfix]
    internal static void Postfix(List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>> __result,
        ChunkSaveMapping ___m_chunkSaveMapping, HashSet<ZoneSystem.ChunkIndex>[] ___m_dirtyChunks,
        List<ZDO>[] ___m_objectsBySector, object ___m_saveData)
    {
        var net = ZNet.instance;
        if (ReferenceEquals(net, null) || !net.IsServer()) return;
        if (ZoneEmptyChunkSaveCompatibility.Check() != ZoneEmptyChunkSaveCompatibility.Strategy.ZoneSavior) return;

        var scheduled = new HashSet<ZoneSystem.ChunkIndex>();
        var splitParents = new HashSet<ZoneSystem.ChunkIndex>();
        foreach (var entry in __result) Record(entry.Item1, scheduled, splitParents);

        var added = 0;
        foreach (var entry in ___m_chunkSaveMapping.Chunks)
        {
            var chunk = entry.Key;
            var previous = entry.Value;
            // Only replace a previously saved, now dirty chunk with old objects to discard.
            // Portal saves already handle empty lists; retired/split mappings are not active files.
            if (!previous.SaveChunk || previous.m_numZDOs <= 0 || !IsOrdinaryChunk(chunk) ||
                !___m_dirtyChunks[0].Contains(chunk) || Overlaps(chunk, scheduled, splitParents) ||
                !IsEmpty(chunk, ___m_objectsBySector)) continue;

            __result.Add(Tuple.Create(chunk, new List<ZDO>()));
            Record(chunk, scheduled, splitParents);
            added++;
        }
        if (added == 0) return;

        // SaveChunks now writes a zero-object file and updates its mapping/version as usual.
        // Do not mutate live mappings or dirty flags: native failure/retry handling owns them.
        FileCount!.SetValue(___m_saveData, (int)FileCount.GetValue(___m_saveData)! + added);
        DirtyFileCount!.SetValue(___m_saveData, (int)DirtyFileCount.GetValue(___m_saveData)! + added);
    }

    private static bool IsOrdinaryChunk(ZoneSystem.ChunkIndex chunk) =>
        !chunk.Equals(ZoneSystem.ChunkPortal) && chunk.m_chunkSize <= 3 &&
        (chunk.Chunk & 0xff) < 64 && (chunk.Chunk >> 8) < 64 &&
        ZoneSystem.ChunkIndexFromIndexAndSize(chunk, chunk.m_chunkSize).Equals(chunk);

    private static void Record(ZoneSystem.ChunkIndex chunk, HashSet<ZoneSystem.ChunkIndex> scheduled,
        HashSet<ZoneSystem.ChunkIndex> splitParents)
    {
        if (!IsOrdinaryChunk(chunk)) return;
        scheduled.Add(chunk);
        for (var size = chunk.m_chunkSize + 1; size <= 3; size++)
            splitParents.Add(ZoneSystem.ChunkIndexFromIndexAndSize(chunk, (byte)size));
    }

    private static bool Overlaps(ZoneSystem.ChunkIndex chunk, HashSet<ZoneSystem.ChunkIndex> scheduled,
        HashSet<ZoneSystem.ChunkIndex> splitParents)
    {
        if (scheduled.Contains(chunk) || splitParents.Contains(chunk)) return true;
        for (var size = chunk.m_chunkSize + 1; size <= 3; size++)
            if (scheduled.Contains(ZoneSystem.ChunkIndexFromIndexAndSize(chunk, (byte)size))) return true;
        return false;
    }

    private static bool IsEmpty(ZoneSystem.ChunkIndex chunk, List<ZDO>[] sectors)
    {
        var (x, y) = ZoneSystem.GetZoneFromChunk(chunk);
        var width = 8 << chunk.m_chunkSize;
        for (var row = y; row < y + width; row++)
            for (var column = x; column < x + width; column++)
            {
                var objects = sectors[ZoneSystem.SectorToIndex(column, row).Sector];
                // Match native counting conservatively, including transient and pending-delete ZDOs.
                if (objects != null && objects.Count != 0) return false;
            }
        return true;
    }
}

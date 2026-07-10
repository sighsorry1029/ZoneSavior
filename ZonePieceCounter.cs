using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static class ZonePieceCounter
{
    private static readonly Dictionary<int, StructureEntry> KnownStructures = [];
    private static readonly Dictionary<Vector2i, int> ZoneCounts = [];

    private static ManualLogSource? _logger;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        Clear();
    }

    public static void Clear()
    {
        KnownStructures.Clear();
        ZoneCounts.Clear();
    }

    public static void RebuildCounts()
    {
        ZoneCounts.Clear();
        if (!ZoneLimitConfiguration.Enabled)
        {
            KnownStructures.Clear();
            return;
        }

        List<int> invalidKeys = [];
        foreach (KeyValuePair<int, StructureEntry> pair in KnownStructures)
        {
            if (!TryBuildStructureState(pair.Value.GameObject, out StructureState state))
            {
                invalidKeys.Add(pair.Key);
                continue;
            }

            pair.Value.Update(state);
            if (pair.Value.Counted)
            {
                AdjustZoneCount(pair.Value.Zone, 1);
            }
        }

        foreach (int invalidKey in invalidKeys)
        {
            KnownStructures.Remove(invalidKey);
        }
    }

    public static void Refresh(Piece piece)
    {
        if (!ZoneLimitConfiguration.Enabled)
        {
            return;
        }

        if (piece == null)
        {
            return;
        }

        Refresh(piece.gameObject);
    }

    public static void Refresh(WearNTear wearNTear)
    {
        if (!ZoneLimitConfiguration.Enabled)
        {
            return;
        }

        if (wearNTear == null)
        {
            return;
        }

        Refresh(wearNTear.gameObject);
    }

    public static void Remove(Component component)
    {
        if (!ZoneLimitConfiguration.Enabled)
        {
            return;
        }

        if (component == null)
        {
            return;
        }

        int key = component.gameObject.GetInstanceID();
        if (!KnownStructures.TryGetValue(key, out StructureEntry entry))
        {
            return;
        }

        KnownStructures.Remove(key);
        if (entry.Counted)
        {
            AdjustZoneCount(entry.Zone, -1);
        }
    }

    public static void HandlePlacement(Piece piece)
    {
        if (!ZoneLimitConfiguration.Enabled)
        {
            return;
        }

        if (piece == null)
        {
            return;
        }

        Refresh(piece);
        if (!TryGetStructureEntry(piece.gameObject, out StructureEntry entry))
        {
            return;
        }

        if (!ZoneLimitConfiguration.TryGetRule(entry.Zone, out ZoneLimitRule rule))
        {
            return;
        }

        int zoneCount = GetCount(entry.Zone);
        bool rejected = zoneCount > rule.Limit;
        if (rejected)
        {
            RejectPlacement(piece, zoneCount, rule.Limit);
        }

        if (piece.IsCreator())
        {
            int displayCount = rejected ? Mathf.Max(0, zoneCount - 1) : zoneCount;
            BuildCounterHud.ShowCount(displayCount, rule.Limit);
        }
    }

    private static void Refresh(GameObject gameObject)
    {
        if (!TryBuildStructureState(gameObject, out StructureState state))
        {
            UnregisterStructure(gameObject);
            return;
        }

        int key = gameObject.GetInstanceID();
        if (KnownStructures.TryGetValue(key, out StructureEntry existing))
        {
            if (existing.Counted)
            {
                AdjustZoneCount(existing.Zone, -1);
            }

            existing.Update(state);
        }
        else
        {
            existing = new StructureEntry(gameObject);
            existing.Update(state);
            KnownStructures[key] = existing;
        }

        if (existing.Counted)
        {
            AdjustZoneCount(existing.Zone, 1);
        }
    }

    private static void UnregisterStructure(GameObject gameObject)
    {
        int key = gameObject.GetInstanceID();
        if (!KnownStructures.TryGetValue(key, out StructureEntry entry))
        {
            return;
        }

        KnownStructures.Remove(key);
        if (entry.Counted)
        {
            AdjustZoneCount(entry.Zone, -1);
        }
    }

    private static bool TryGetStructureEntry(GameObject gameObject, out StructureEntry entry)
    {
        return KnownStructures.TryGetValue(gameObject.GetInstanceID(), out entry);
    }

    private static bool TryBuildStructureState(GameObject gameObject, out StructureState state)
    {
        state = default;

        if (!gameObject)
        {
            return false;
        }

        WearNTear? wearNTear = gameObject.GetComponent<WearNTear>();
        if (wearNTear == null)
        {
            return false;
        }

        Piece? piece = gameObject.GetComponent<Piece>();
        ZNetView? nview = gameObject.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (nview == null || !nview.IsValid() || zdo == null)
        {
            return false;
        }

        Vector2i zone = ZoneSystem.GetZone(gameObject.transform.position);
        bool playerPlaced = IsPlayerPlaced(piece, nview);
        bool forceCount = ZoneExternalPieceMarkers.ShouldForceCount(zdo);
        bool counted = ShouldCount(zone, playerPlaced, forceCount);

        state = new StructureState(zone, counted);
        return true;
    }

    private static bool IsPlayerPlaced(Piece? piece, ZNetView nview)
    {
        if (piece != null)
        {
            return piece.IsPlacedByPlayer();
        }

        return nview.GetZDO()?.GetLong(ZDOVars.s_creator, 0L) != 0L;
    }

    private static bool ShouldCount(Vector2i zone, bool playerPlaced, bool forceCount)
    {
        if (!ZoneLimitConfiguration.TryGetRule(zone, out ZoneLimitRule rule))
        {
            return false;
        }

        return playerPlaced || forceCount || rule.CountCreatorless;
    }

    private static int GetCount(Vector2i zone)
    {
        return ZoneCounts.TryGetValue(zone, out int count) ? count : 0;
    }

    private static void AdjustZoneCount(Vector2i zone, int delta)
    {
        int newCount = GetCount(zone) + delta;
        if (newCount <= 0)
        {
            ZoneCounts.Remove(zone);
            return;
        }

        ZoneCounts[zone] = newCount;
    }

    private static void RejectPlacement(Piece piece, int zoneCount, int limit)
    {
        string message = $"WearNTear zone limit reached ({zoneCount}/{limit})";
        if (piece.IsCreator() && Player.m_localPlayer != null)
        {
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
        }

        WearNTear? wearNTear = piece.GetComponent<WearNTear>();
        if (wearNTear != null)
        {
            wearNTear.Remove(blockDrop: false);
            return;
        }

        ZNetView? nview = piece.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid() || !nview.IsOwner())
        {
            _logger?.LogWarning($"Unable to reject {piece.name} cleanly because the local client does not own it and it has no WearNTear component.");
            return;
        }

        piece.DropResources();
        if (ZNetScene.instance != null)
        {
            ZNetScene.instance.Destroy(piece.gameObject);
        }
        else
        {
            Object.Destroy(piece.gameObject);
        }
    }

    private sealed class StructureEntry
    {
        public StructureEntry(GameObject gameObject)
        {
            GameObject = gameObject;
        }

        public GameObject GameObject { get; }
        public Vector2i Zone { get; private set; }
        public bool Counted { get; private set; }

        public void Update(StructureState state)
        {
            Zone = state.Zone;
            Counted = state.Counted;
        }
    }

    private readonly struct StructureState
    {
        public StructureState(Vector2i zone, bool counted)
        {
            Zone = zone;
            Counted = counted;
        }

        public Vector2i Zone { get; }
        public bool Counted { get; }
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class ZonePieceCounterScenePatch
{
    private static void Postfix()
    {
        ZonePieceCounter.Clear();
    }
}

[HarmonyPatch(typeof(Piece), nameof(Piece.Awake))]
internal static class ZonePieceCounterPieceAwakePatch
{
    private static void Postfix(Piece __instance)
    {
        ZonePieceCounter.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
internal static class ZonePieceCounterPieceCreatorPatch
{
    private static void Postfix(Piece __instance)
    {
        ZonePieceCounter.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(Piece), nameof(Piece.OnPlaced))]
internal static class ZonePieceCounterPiecePlacedPatch
{
    private static void Postfix(Piece __instance)
    {
        ZonePieceCounter.HandlePlacement(__instance);
    }
}

[HarmonyPatch(typeof(Piece), "OnDestroy")]
internal static class ZonePieceCounterPieceDestroyPatch
{
    private static void Prefix(Piece __instance)
    {
        ZonePieceCounter.Remove(__instance);
    }
}

[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Awake))]
internal static class ZonePieceCounterWearNTearAwakePatch
{
    private static void Postfix(WearNTear __instance)
    {
        ZonePieceCounter.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(WearNTear), "OnDestroy")]
internal static class ZonePieceCounterWearNTearDestroyPatch
{
    private static void Prefix(WearNTear __instance)
    {
        ZonePieceCounter.Remove(__instance);
    }
}


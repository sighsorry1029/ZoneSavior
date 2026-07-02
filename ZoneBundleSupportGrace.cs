using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneBundleSupportGrace
{
    private const string SyncRpcName = ZoneSaviorPlugin.ModGUID + "_ZoneBundleSupportGraceSync";
    private const string SnapshotRequestRpcName = ZoneSaviorPlugin.ModGUID + "_ZoneBundleSupportGraceSnapshotRequest";

    private static readonly ZoneRpcRegistrar RpcRegistrar = new();
    private static readonly Dictionary<Vector2i, DateTime> GraceUntilUtc = new();

    private static ManualLogSource _logger = null!;
    private static long _requestedSnapshotServer;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    public static void RegisterRpcs()
    {
        RpcRegistrar.EnsureRegistered(rpc =>
        {
            rpc.Register<ZPackage>(SyncRpcName, ReceiveGraceSync);
            rpc.Register(SnapshotRequestRpcName, ReceiveSnapshotRequest);
            _requestedSnapshotServer = 0L;
        });
    }

    public static void Update()
    {
        RegisterRpcs();
        RemoveExpired(DateTime.UtcNow);
        RequestSnapshotIfNeeded();
    }

    public static void Shutdown()
    {
        GraceUntilUtc.Clear();
        RpcRegistrar.Reset();
        _requestedSnapshotServer = 0L;
    }

    public static void RegisterZones(IEnumerable<Vector2i> zones)
    {
        float minutes = ZoneBundleConfig.SupportGraceMinutes;
        if (minutes <= 0f)
        {
            return;
        }

        List<Vector2i> zoneList = zones.Distinct().ToList();
        if (zoneList.Count == 0)
        {
            return;
        }

        DateTime expiresAt = DateTime.UtcNow.AddMinutes(minutes);
        ApplyGrace(zoneList, expiresAt);
        BroadcastGrace(zoneList, expiresAt);
        _logger.LogInfo($"Zone bundle support grace active for {zoneList.Count} zone(s), {minutes:0.#} minute(s).");
    }

    public static bool IsActive(WearNTear wearNTear)
    {
        if (!wearNTear)
        {
            return false;
        }

        return TryGetRemaining(ZoneSystem.GetZone(wearNTear.transform.position), out _);
    }

    public static bool TryGetRemaining(Vector2i zone, out TimeSpan remaining)
    {
        DateTime now = DateTime.UtcNow;
        if (!GraceUntilUtc.TryGetValue(zone, out DateTime expiresAt))
        {
            remaining = TimeSpan.Zero;
            return false;
        }

        if (expiresAt <= now)
        {
            GraceUntilUtc.Remove(zone);
            remaining = TimeSpan.Zero;
            return false;
        }

        remaining = expiresAt - now;
        return true;
    }

    private static void ApplyGrace(IEnumerable<Vector2i> zones, DateTime expiresAt)
    {
        foreach (Vector2i zone in zones)
        {
            GraceUntilUtc[zone] = expiresAt;
        }
    }

    private static void BroadcastGrace(List<Vector2i> zones, DateTime expiresAt)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null)
        {
            return;
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, SyncRpcName, CreateSyncPackage(zones, expiresAt));
    }

    private static void RequestSnapshotIfNeeded()
    {
        if (ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null)
        {
            return;
        }

        long serverPeer = ZRoutedRpc.instance.GetServerPeerID();
        if (serverPeer == 0L || _requestedSnapshotServer == serverPeer)
        {
            return;
        }

        _requestedSnapshotServer = serverPeer;
        ZRoutedRpc.instance.InvokeRoutedRPC(serverPeer, SnapshotRequestRpcName);
    }

    private static void ReceiveSnapshotRequest(long sender)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null)
        {
            return;
        }

        RemoveExpired(DateTime.UtcNow);
        if (GraceUntilUtc.Count == 0)
        {
            return;
        }

        ZPackage package = CreateSnapshotPackage();
        ZRoutedRpc.instance.InvokeRoutedRPC(sender, SyncRpcName, package);
    }

    private static void ReceiveGraceSync(long sender, ZPackage package)
    {
        if (ZNet.instance != null &&
            !ZNet.instance.IsServer() &&
            ZRoutedRpc.instance != null &&
            sender != ZRoutedRpc.instance.GetServerPeerID())
        {
            return;
        }

        try
        {
            int count = package.ReadInt();
            for (int i = 0; i < count; i++)
            {
                DateTime expiresAt = new(package.ReadLong(), DateTimeKind.Utc);
                ApplyGrace([new Vector2i(package.ReadInt(), package.ReadInt())], expiresAt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to read zone bundle support grace sync: {ex.Message}");
        }
    }

    private static ZPackage CreateSyncPackage(IEnumerable<Vector2i> zones, DateTime expiresAt)
    {
        List<Vector2i> zoneList = zones.ToList();
        ZPackage package = new();
        package.Write(zoneList.Count);
        foreach (Vector2i zone in zoneList)
        {
            package.Write(expiresAt.Ticks);
            package.Write(zone.x);
            package.Write(zone.y);
        }

        return package;
    }

    private static ZPackage CreateSnapshotPackage()
    {
        ZPackage package = new();
        package.Write(GraceUntilUtc.Count);
        foreach (KeyValuePair<Vector2i, DateTime> item in GraceUntilUtc.OrderBy(item => item.Key.x).ThenBy(item => item.Key.y))
        {
            package.Write(item.Value.Ticks);
            package.Write(item.Key.x);
            package.Write(item.Key.y);
        }

        return package;
    }

    private static void RemoveExpired(DateTime now)
    {
        foreach (Vector2i zone in GraceUntilUtc
                     .Where(item => item.Value <= now)
                     .Select(item => item.Key)
                     .ToList())
        {
            GraceUntilUtc.Remove(zone);
        }
    }
}

[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.HaveSupport))]
internal static class ZoneBundleSupportGraceHaveSupportPatch
{
    private static void Postfix(WearNTear __instance, ref bool __result)
    {
        if (!__result && ZoneBundleSupportGrace.IsActive(__instance))
        {
            __result = true;
        }
    }
}

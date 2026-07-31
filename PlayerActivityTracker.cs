using System;
using BepInEx.Logging;
using HarmonyLib;

namespace ZoneSavior;

internal static class PlayerActivityTracker
{
    private static ManualLogSource _logger = null!;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    public static void TrackOnlinePlayers(DateTime utcNow)
    {
        if (!IsServerReady())
        {
            return;
        }

        try
        {
            TrackRemotePeers(utcNow);
            TrackLocalPlayer(utcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to update player activity: {ex.Message}");
        }
    }

    private static void TrackRemotePeers(DateTime utcNow)
    {
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            TrackPeer(peer, utcNow, requireReady: true);
        }
    }

    internal static void TrackPeer(ZRpc rpc)
    {
        if (!IsServerReady())
        {
            return;
        }

        ZNetPeer peer = ZNet.instance.GetPeer(rpc);
        TrackPeer(peer, DateTime.UtcNow, requireReady: false);
    }

    private static void TrackPeer(ZNetPeer peer, DateTime utcNow, bool requireReady)
    {
        try
        {
            if (!TryGetPeerActivity(peer, requireReady, out string platformId, out long playerId, out string name))
            {
                return;
            }

            AutoArchiveStore.RecordPlayerSeen(platformId, playerId, name, utcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to update peer activity: {ex.Message}");
        }
    }

    private static bool TryGetPeerActivity(ZNetPeer peer, bool requireReady, out string platformId, out long playerId, out string name)
    {
        platformId = "";
        playerId = 0L;
        name = "";
        if (peer == null || (requireReady && !peer.IsReady()))
        {
            return false;
        }

        playerId = TryReadPlayerId(peer.m_characterID);
        platformId = ZonePlayerIdentity.ResolvePeerPlatformId(peer, playerId);
        name = peer.m_playerName;
        return !string.IsNullOrWhiteSpace(platformId) &&
               (requireReady || playerId != 0L || !string.IsNullOrWhiteSpace(name));
    }

    private static void TrackLocalPlayer(DateTime utcNow)
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        long playerId = player.GetPlayerID();
        string name = player.GetPlayerName();
        string platformId = ZonePlayerIdentity.ResolveLocalPlatformId(playerId);
        AutoArchiveStore.RecordPlayerSeen(platformId, playerId, name, utcNow);
    }

    private static long TryReadPlayerId(ZDOID characterId)
    {
        if (characterId.IsNone() || ZDOMan.instance == null)
        {
            return 0L;
        }

        ZDO zdo = ZDOMan.instance.GetZDO(characterId);
        return zdo?.GetLong(ZDOVars.s_playerID, 0L) ?? 0L;
    }

    private static bool IsServerReady()
    {
        return ZNet.instance != null && ZNet.instance.IsServer() && ZDOMan.instance != null && ZoneSystem.instance != null;
    }
}

[HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
internal static class PlayerActivityPeerInfoPatch
{
    private static void Postfix(ZRpc rpc)
    {
        PlayerActivityTracker.TrackPeer(rpc);
    }
}

[HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
internal static class PlayerActivityCharacterIdPatch
{
    private static void Postfix(ZRpc rpc)
    {
        PlayerActivityTracker.TrackPeer(rpc);
    }
}


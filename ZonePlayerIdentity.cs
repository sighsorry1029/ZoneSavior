using System;
using Splatform;

namespace ZoneSavior;

internal static class ZonePlayerIdentity
{
    public static string ResolvePeerPlatformId(ZNetPeer peer, long playerId)
    {
        if (peer == null)
        {
            return FallbackPlatformId(playerId);
        }

        string host = peer.m_socket?.GetHostName() ?? "";
        if (!string.IsNullOrWhiteSpace(host))
        {
            return NormalizePlatformId(ZNet.m_onlineBackend == OnlineBackendType.Steamworks ? $"steam:{host}" : host);
        }

        return $"session:{peer.m_uid}";
    }

    public static string ResolveLocalPlatformId(long playerId)
    {
        string platformId = "";
        try
        {
            platformId = UserInfo.GetLocalUser()?.UserId.ToString() ?? "";
        }
        catch
        {
            // Local platform identity can be unavailable during early startup or headless flows.
        }

        platformId = NormalizePlatformId(platformId);
        return string.IsNullOrWhiteSpace(platformId) ? FallbackPlatformId(playerId) : platformId;
    }

    public static string FallbackPlatformId(long playerId)
    {
        return playerId != 0L ? $"local:{playerId}" : "";
    }

    public static string NormalizePlatformId(string value)
    {
        string text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (text.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase))
        {
            return "steam:" + text.Substring("Steam_".Length);
        }

        if (text.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
        {
            return "steam:" + text.Substring("steam:".Length).Trim();
        }

        return text;
    }
}


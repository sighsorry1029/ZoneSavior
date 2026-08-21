using System;
using System.Collections.Generic;
using System.Text;

namespace ZoneSavior;

public static class ZoneSaviorCreatorNameLookupProtocol
{
    public const int Version = 1;
    public const int RequestBytes = 28;
    public const int MaxResponseBytes = 512;
    public const int MaxNameCharacters = 128;

    public const int StatusSuccess = 0;
    public const int StatusUnsupportedVersion = 1;
    public const int StatusMalformedRequest = 2;
    public const int StatusUnauthorized = 3;
    public const int StatusRateLimited = 4;
    public const int StatusWorldMismatch = 5;
    public const int StatusServerError = 6;

    public const string RequestRpcName = "sighsorry.ZoneSavior_CreatorNameLookupRequest";
    public const string ResponseRpcName = "sighsorry.ZoneSavior_CreatorNameLookupResponse";
}

internal static class CreatorNameLookupRpc
{
    private const int MaxRequestsPerWindow = 8;
    private static readonly TimeSpan RateWindowDuration = TimeSpan.FromSeconds(5);
    private static readonly Dictionary<long, RequestRateWindow> RequestWindows = [];

    private static ZRoutedRpc? _registeredRpc;

    internal static void Register()
    {
        ZNet? znet = ZNet.instance;
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (znet == null || !znet.IsServer() || routedRpc == null || ReferenceEquals(_registeredRpc, routedRpc))
        {
            return;
        }

        _registeredRpc = routedRpc;
        RequestWindows.Clear();
        routedRpc.Register<ZPackage>(ZoneSaviorCreatorNameLookupProtocol.RequestRpcName, RPC_HandleRequest);
    }

    internal static void ClearRateLimits()
    {
        // ZRoutedRpc has no unregister operation. Keep the registered instance so a
        // hot shutdown/start on the same session cannot register this name twice.
        RequestWindows.Clear();
    }

    private static void RPC_HandleRequest(long sender, ZPackage package)
    {
        ZNet? znet = ZNet.instance;
        if (znet == null || !znet.IsServer() || ZRoutedRpc.instance == null)
        {
            return;
        }

        if (!TryReadRequest(package, out int version, out long requestId, out long worldUid, out long playerId))
        {
            return;
        }

        bool authorized;
        try
        {
            authorized = IsAuthorizedSender(sender);
        }
        catch
        {
            return;
        }

        if (!authorized)
        {
            SendResponse(sender, requestId, worldUid, ZoneSaviorCreatorNameLookupProtocol.StatusUnauthorized, playerId, false, "");
            return;
        }

        if (!TryConsumeRequest(sender, DateTime.UtcNow))
        {
            SendResponse(sender, requestId, worldUid, ZoneSaviorCreatorNameLookupProtocol.StatusRateLimited, playerId, false, "");
            return;
        }

        if (requestId == 0L || playerId == 0L)
        {
            SendResponse(sender, requestId, worldUid, ZoneSaviorCreatorNameLookupProtocol.StatusMalformedRequest, playerId, false, "");
            return;
        }

        if (version != ZoneSaviorCreatorNameLookupProtocol.Version)
        {
            SendResponse(sender, requestId, worldUid, ZoneSaviorCreatorNameLookupProtocol.StatusUnsupportedVersion, playerId, false, "");
            return;
        }

        long serverWorldUid;
        try
        {
            serverWorldUid = znet.GetWorldUID();
        }
        catch
        {
            SendResponse(sender, requestId, worldUid, ZoneSaviorCreatorNameLookupProtocol.StatusServerError, playerId, false, "");
            return;
        }

        if (worldUid != serverWorldUid)
        {
            SendResponse(sender, requestId, worldUid, ZoneSaviorCreatorNameLookupProtocol.StatusWorldMismatch, playerId, false, "");
            return;
        }

        try
        {
            bool found = AutoArchiveStore.TryResolveLastKnownPlayerName(playerId, out string storedName);
            string name = found ? NormalizeName(storedName) : "";
            SendResponse(
                sender,
                requestId,
                worldUid,
                ZoneSaviorCreatorNameLookupProtocol.StatusSuccess,
                playerId,
                name.Length > 0,
                name);
        }
        catch (Exception ex)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning($"Creator name lookup failed: {ex.Message}");
            SendResponse(sender, requestId, worldUid, ZoneSaviorCreatorNameLookupProtocol.StatusServerError, playerId, false, "");
        }
    }

    private static bool TryReadRequest(
        ZPackage package,
        out int version,
        out long requestId,
        out long worldUid,
        out long playerId)
    {
        version = 0;
        requestId = 0L;
        worldUid = 0L;
        playerId = 0L;
        if (package == null || package.Size() != ZoneSaviorCreatorNameLookupProtocol.RequestBytes)
        {
            return false;
        }

        try
        {
            version = package.ReadInt();
            requestId = package.ReadLong();
            worldUid = package.ReadLong();
            playerId = package.ReadLong();
            return package.GetPos() == package.Size();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAuthorizedSender(long sender)
    {
        return (Player.m_localPlayer != null &&
                ZNet.instance != null &&
                ZNet.instance.IsServer() &&
                ZDOMan.instance != null &&
                sender == ZDOMan.GetSessionID()) ||
               ZoneBundleCommands.IsAuthorizedSender(sender);
    }

    private static bool TryConsumeRequest(long sender, DateTime utcNow)
    {
        if (!RequestWindows.TryGetValue(sender, out RequestRateWindow window) ||
            utcNow - window.StartedUtc >= RateWindowDuration)
        {
            RequestWindows[sender] = new RequestRateWindow(utcNow, 1);
            PruneExpiredWindows(utcNow);
            return true;
        }

        if (window.RequestCount >= MaxRequestsPerWindow)
        {
            return false;
        }

        window.RequestCount++;
        return true;
    }

    private static void PruneExpiredWindows(DateTime utcNow)
    {
        if (RequestWindows.Count <= 32)
        {
            return;
        }

        List<long> expired = [];
        foreach (KeyValuePair<long, RequestRateWindow> pair in RequestWindows)
        {
            if (utcNow - pair.Value.StartedUtc >= RateWindowDuration)
            {
                expired.Add(pair.Key);
            }
        }

        foreach (long sender in expired)
        {
            RequestWindows.Remove(sender);
        }
    }

    private static void SendResponse(
        long target,
        long requestId,
        long worldUid,
        int status,
        long playerId,
        bool found,
        string name)
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null || target == 0L)
        {
            return;
        }

        ZPackage response = CreateResponse(requestId, worldUid, status, playerId, found, name);
        if (response.Size() > ZoneSaviorCreatorNameLookupProtocol.MaxResponseBytes)
        {
            response = CreateResponse(
                requestId,
                worldUid,
                ZoneSaviorCreatorNameLookupProtocol.StatusServerError,
                playerId,
                false,
                "");
        }

        try
        {
            routedRpc.InvokeRoutedRPC(target, ZoneSaviorCreatorNameLookupProtocol.ResponseRpcName, response);
        }
        catch (Exception ex)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogDebug($"Could not send creator name lookup response: {ex.Message}");
        }
    }

    private static ZPackage CreateResponse(
        long requestId,
        long worldUid,
        int status,
        long playerId,
        bool found,
        string name)
    {
        ZPackage response = new();
        response.Write(ZoneSaviorCreatorNameLookupProtocol.Version);
        response.Write(requestId);
        response.Write(worldUid);
        response.Write(status);
        response.Write(playerId);
        response.Write(found);
        response.Write(name);
        return response;
    }

    private static string NormalizeName(string value)
    {
        string trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return "";
        }

        int capacity = Math.Min(trimmed.Length, ZoneSaviorCreatorNameLookupProtocol.MaxNameCharacters);
        StringBuilder builder = new(capacity);
        bool previousWasSpace = false;
        foreach (char character in trimmed)
        {
            char normalized = char.IsControl(character) || char.IsWhiteSpace(character) ? ' ' : character;
            if (normalized == ' ' && previousWasSpace)
            {
                continue;
            }

            if (builder.Length >= ZoneSaviorCreatorNameLookupProtocol.MaxNameCharacters)
            {
                break;
            }

            builder.Append(normalized);
            previousWasSpace = normalized == ' ';
        }

        if (builder.Length > 0 && char.IsHighSurrogate(builder[builder.Length - 1]))
        {
            builder.Length--;
        }

        return builder.ToString().Trim();
    }

    private sealed class RequestRateWindow
    {
        internal RequestRateWindow(DateTime startedUtc, int requestCount)
        {
            StartedUtc = startedUtc;
            RequestCount = requestCount;
        }

        internal DateTime StartedUtc { get; }
        internal int RequestCount { get; set; }
    }
}

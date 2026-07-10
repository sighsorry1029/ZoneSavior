using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ZoneSavior;

internal static partial class ZoneBundleCommands
{
    private static readonly Regex CommandPattern = new(@"^\s*(\([^)]+\))\s+([^\s]+)(?:\s+to\s+(\([^)]+\)))?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LoadZonePattern = new(@"^\s*([^\s]+)(?:\s+(?:(restore)|source\s+(\([^)]+\))))?(?:\s+to\s+(\([^)]+\)))?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex YOffsetEqualsOptionPattern = new(@"(?:^|\s)(?:offset|yoffset|y-offset)\s*=\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex YOffsetFlagOptionPattern = new(@"(?:^|\s)--(?:offset|yoffset|y-offset)\s+([+-]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RangePattern = new(@"^\(\s*([^,]+)\s*,\s*([^)]+)\s*\)$", RegexOptions.Compiled);

    internal static ZoneBundleCommandRequest ParseLoadRequest(string argsAll)
    {
        float yOffset = ExtractYOffsetOption(ref argsAll);

        Match match = LoadZonePattern.Match(argsAll);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Syntax: {LoadOperation} tag [restore|source (x,z)] [to (x,z)] [offset=Y]");
        }

        bool restoreOriginal = match.Groups[2].Success;
        bool loadSourceZone = match.Groups[3].Success;
        if ((restoreOriginal && loadSourceZone) || (restoreOriginal && match.Groups[4].Success))
        {
            throw new InvalidOperationException($"Syntax: {LoadOperation} tag [restore|source (x,z)] [to (x,z)] [offset=Y]");
        }

        Vector2i? sourceZone = loadSourceZone ? ParseSingleZone(match.Groups[3].Value) : null;
        ZoneBundleCommandRequest request = new()
        {
            Operation = LoadOperation,
            Tag = match.Groups[1].Value,
            TargetZone = restoreOriginal
                ? null
                : match.Groups[4].Success
                    ? ToModel(ParseSingleZone(match.Groups[4].Value))
                    : ToModel(GetCurrentPlayerZone()),
            YOffset = yOffset,
            RestoreOriginal = restoreOriginal,
            LoadSourceZone = loadSourceZone
        };

        if (sourceZone.HasValue)
        {
            request.SourceRange = CreateRange(sourceZone.Value.x, sourceZone.Value.y, sourceZone.Value.x, sourceZone.Value.y);
        }

        return request;
    }

    internal static ZoneBundleCommandRequest ParseRequest(string argsAll, string operation, bool requireSingleZone, bool requireTarget)
    {
        float yOffset = ExtractYOffsetOption(ref argsAll);

        Match match = CommandPattern.Match(argsAll);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                operation switch
                {
                    SaveOperation => $"Syntax: {SaveOperation} (x,z) tag or {SaveOperation} (x~x,z~z) tag",
                    _ => $"Syntax: {LoadOperation} tag [restore|source (x,z)] [to (x,z)] [offset=Y]"
                });
        }

        ZoneBundleCommandRequest request = new()
        {
            Operation = operation,
            SourceRange = ParseZoneRange(match.Groups[1].Value, requireSingleZone),
            Tag = match.Groups[2].Value,
            YOffset = yOffset
        };

        if (match.Groups[3].Success)
        {
            request.TargetZone = ToModel(ParseSingleZone(match.Groups[3].Value));
        }
        else if (requireTarget)
        {
            request.TargetZone = ToModel(GetCurrentPlayerZone());
        }

        return request;
    }

    private static ZoneBundleRange ParseZoneRange(string spec, bool requireSingleZone)
    {
        Match match = RangePattern.Match(spec);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Invalid zone spec '{spec}'.");
        }

        (int minX, int maxX) = ParseAxis(match.Groups[1].Value);
        (int minZ, int maxZ) = ParseAxis(match.Groups[2].Value);
        if (requireSingleZone && (minX != maxX || minZ != maxZ))
        {
            throw new InvalidOperationException("This command requires a single source zone.");
        }

        return CreateRange(minX, minZ, maxX, maxZ);
    }

    private static Vector2i ParseSingleZone(string spec)
    {
        ZoneBundleRange range = ParseZoneRange(spec, requireSingleZone: true);
        return new Vector2i(range.MinX, range.MinZ);
    }

    private static (int Min, int Max) ParseAxis(string axis)
    {
        string[] parts = axis.Trim().Split('~');
        if (parts.Length == 1)
        {
            int value = ParseInt(parts[0]);
            return (value, value);
        }

        if (parts.Length != 2)
        {
            throw new InvalidOperationException($"Invalid axis range '{axis}'.");
        }

        int first = ParseInt(parts[0]);
        int second = ParseInt(parts[1]);
        return first <= second ? (first, second) : (second, first);
    }

    private static float ExtractYOffsetOption(ref string argsAll)
    {
        Match match = YOffsetEqualsOptionPattern.Match(argsAll);
        if (!match.Success)
        {
            match = YOffsetFlagOptionPattern.Match(argsAll);
        }

        if (!match.Success)
        {
            return 0f;
        }

        float offset = ParseFloat(match.Groups[1].Value);
        argsAll = YOffsetEqualsOptionPattern.Replace(argsAll, " ");
        argsAll = YOffsetFlagOptionPattern.Replace(argsAll, " ").Trim();
        return offset;
    }

    private static void ApplyYOffset(TerrainPlacementContext? context, float yOffset)
    {
        if (context == null || Mathf.Abs(yOffset) <= 0.0001f)
        {
            return;
        }

        context.BaseWorldY += yOffset;
    }

    private static int ParseInt(string value)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            throw new InvalidOperationException($"Invalid integer '{value}'.");
        }

        return parsed;
    }

    private static float ParseFloat(string value)
    {
        if (!float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            throw new InvalidOperationException($"Invalid number '{value}'.");
        }

        return parsed;
    }

    private static ZoneBundleRange CreateRange(int minX, int minZ, int maxX, int maxZ)
    {
        return new ZoneBundleRange
        {
            MinX = Math.Min(minX, maxX),
            MaxX = Math.Max(minX, maxX),
            MinZ = Math.Min(minZ, maxZ),
            MaxZ = Math.Max(minZ, maxZ)
        };
    }

    private static IEnumerable<Vector2i> EnumerateZones(ZoneBundleRange range)
    {
        for (int z = range.MinZ; z <= range.MaxZ; z++)
        {
            for (int x = range.MinX; x <= range.MaxX; x++)
            {
                yield return new Vector2i(x, z);
            }
        }
    }

    private static Vector2i GetCurrentPlayerZone()
    {
        Player player = Player.m_localPlayer;
        if (!player)
        {
            throw new InvalidOperationException("No local player available. Use to (x,z) from a dedicated server console.");
        }

        return ZoneSystem.GetZone(player.transform.position);
    }

    internal static void EnsureCommandAllowed()
    {
        if (!ZNet.instance || !ZNetScene.instance || !ZoneSystem.instance || ZDOMan.instance == null)
        {
            throw new InvalidOperationException("World is not ready.");
        }

        if (!ZNet.instance.IsServer())
        {
            if (ZRoutedRpc.instance == null)
            {
                throw new InvalidOperationException("Server RPC is not ready.");
            }

            return;
        }

        if (ZNet.instance.IsServer() && Player.m_localPlayer == null)
        {
            return;
        }

        if (!ZNet.instance.LocalPlayerIsAdminOrHost())
        {
            throw new InvalidOperationException("Admin only.");
        }
    }

    internal static bool IsAuthorizedSender(long sender)
    {
        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        string hostName = peer?.m_rpc?.m_socket?.GetHostName() ?? "";
        return hostName.Length > 0 && ZNet.instance.IsAdmin(hostName);
    }

    internal static void ShowResult(ZoneBundleCommandResult result, Terminal? terminal = null)
    {
        _logger.LogInfo(result.Message);

        if (terminal != null)
        {
            terminal.AddString(result.Message);
        }
        else if (Console.instance != null)
        {
            Console.instance.AddString(result.Message);
        }

        if (Player.m_localPlayer != null)
        {
            Player.m_localPlayer.Message(result.Success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center, result.Message);
        }
    }

    private static string GetWorldName()
    {
        return ZNet.instance.GetWorldName();
    }

    private static ZoneBundleZone ToModel(Vector2i zone)
    {
        return ZoneSaviorZones.ToModel(zone);
    }

    private static Vector2i ToVector2i(ZoneBundleZone zone)
    {
        return ZoneSaviorZones.ToVector2i(zone);
    }

    private static Vector2i ToSingleSourceZone(ZoneBundleRange range)
    {
        return new Vector2i(range.MinX, range.MinZ);
    }

    private static float Round(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }
}

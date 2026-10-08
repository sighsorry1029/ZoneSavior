using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneSaviorSteamIds
{
    private const string SteamPrefix = "steam:";

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        string raw = value.Trim();
        if (raw.StartsWith(SteamPrefix, StringComparison.OrdinalIgnoreCase))
        {
            raw = raw.Substring(SteamPrefix.Length);
        }

        string digits = new(raw.Where(char.IsDigit).ToArray());
        return digits.Length >= 15 ? digits : "";
    }

    public static bool IsBareSteamId64(string value)
    {
        string raw = value?.Trim() ?? "";
        return raw.Length == 17 && raw.All(char.IsDigit);
    }

    public static bool LooksLikeSteamId(string value)
    {
        return !string.IsNullOrWhiteSpace(Normalize(value));
    }

    public static bool TryNormalizePlatformId(string platformId, out string steamId)
    {
        steamId = "";
        if (string.IsNullOrWhiteSpace(platformId) ||
            !platformId.StartsWith(SteamPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        steamId = Normalize(platformId);
        return !string.IsNullOrWhiteSpace(steamId);
    }
}

internal static class ZoneSaviorPaths
{
    private const int MaxPathSegmentLength = 96;

    public static string SanitizePathSegment(string value)
    {
        string segment = value?.Trim() ?? "";
        if (segment.Length == 0)
        {
            throw new InvalidOperationException("Tag or world name resolves to an empty path segment.");
        }

        if (segment.Length > MaxPathSegmentLength)
        {
            throw new InvalidOperationException($"Tag or world name exceeds {MaxPathSegmentLength} characters.");
        }

        if (segment is "." or ".." ||
            segment.EndsWith(".", StringComparison.Ordinal) ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"Tag or world name '{value}' is not a safe file name.");
        }

        return segment;
    }

    public static string SanitizeTagToken(string value, int maxLength = 32)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = new(value
            .Select(character =>
            {
                if (invalidChars.Contains(character) || char.IsWhiteSpace(character))
                {
                    return '_';
                }

                return char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '_';
            })
            .ToArray());

        sanitized = sanitized.Trim('_');
        while (sanitized.Contains("__"))
        {
            sanitized = sanitized.Replace("__", "_");
        }

        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized.Substring(0, maxLength).Trim('_');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}

internal static class ZoneSaviorFiles
{
    private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllTextAtomic(string path, string contents, Encoding? encoding = null)
    {
        WriteAtomic(path, stream =>
        {
            using StreamWriter writer = new(stream, encoding ?? DefaultEncoding, 4096, leaveOpen: true);
            writer.Write(contents);
            writer.Flush();
        });
    }

    public static void WriteAtomic(string path, Action<Stream> write)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal static class ZoneSaviorZones
{
    // Keep the mod's integer coordinates and serialized models unchanged.
    internal static Vector2s ToGameZone(Vector2i zone)
    {
        if (zone.x < short.MinValue || zone.x > short.MaxValue ||
            zone.y < short.MinValue || zone.y > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(zone), "Zone coordinates exceed Valheim's supported range.");
        }
        return new Vector2s(zone.x, zone.y);
    }

    internal static Vector2i GetZone(Vector3 position)
    {
        Vector2s zone = ZoneSystem.GetZone(position);
        return new Vector2i(zone.x, zone.y);
    }

    internal static Vector3 GetZonePos(Vector2i zone) => ZoneSystem.GetZonePos(ToGameZone(zone));

    internal static void FindObjects(Vector2i zone, List<ZDO> objects)
    {
        ZoneSystem.SectorIndex index = ZoneSystem.SectorToIndex(ToGameZone(zone));
        AddZoneObjects(ZoneSaviorGameAccess.Sectors(ZDOMan.instance)[index.Sector], zone, objects);
        if (ZDOMan.instance.GetPortals().TryGetValue(index, out List<ZDO> portals))
        {
            AddZoneObjects(portals, zone, objects);
        }
    }

    private static void AddZoneObjects(List<ZDO>? source, Vector2i zone, List<ZDO> destination)
    {
        if (source == null) return;
        foreach (ZDO zdo in source)
        {
            // Sector zero also contains out-of-world objects; never act on a different zone.
            if (zdo != null && GetZone(zdo.GetPosition()) == zone) destination.Add(zdo);
        }
    }

    public static ZoneBundleZone ToModel(Vector2i zone)
    {
        return new ZoneBundleZone
        {
            X = zone.x,
            Z = zone.y
        };
    }

    public static Vector2i ToVector2i(ZoneBundleZone zone)
    {
        return new Vector2i(zone.X, zone.Z);
    }

}

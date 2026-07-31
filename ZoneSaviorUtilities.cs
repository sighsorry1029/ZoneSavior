using System;
using System.IO;
using System.Linq;
using System.Text;

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

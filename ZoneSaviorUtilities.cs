using System;
using System.IO;
using System.Linq;

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
    public static string SanitizePathSegment(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = new(value.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray());
        sanitized = sanitized.Trim();
        if (sanitized.Length == 0)
        {
            throw new InvalidOperationException("Tag or world name resolves to an empty path segment.");
        }

        return sanitized;
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

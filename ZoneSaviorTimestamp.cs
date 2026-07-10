using System;
using System.Globalization;

namespace ZoneSavior;

internal static class ZoneSaviorTimestamp
{
    public static string Now()
    {
        return Format(DateTime.UtcNow);
    }

    public static string Format(DateTime utc)
    {
        if (utc == DateTime.MinValue)
        {
            return "";
        }

        DateTime normalizedUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        DateTimeOffset local = new DateTimeOffset(normalizedUtc).ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    public static DateTime ParseUtc(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTime.MinValue;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset withOffset) ||
            DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out withOffset))
        {
            return DateTime.SpecifyKind(withOffset.UtcDateTime, DateTimeKind.Utc);
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out DateTime local) ||
            DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out local))
        {
            return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        }

        return DateTime.MinValue;
    }

}

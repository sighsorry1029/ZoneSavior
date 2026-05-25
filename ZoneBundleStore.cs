using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneBundleStore
{
    public static string GetManifestPath(string tag)
    {
        return Path.Combine(GetTagDirectory(tag), "manifest.yml");
    }

    public static string GetBundlePath(string tag, int index)
    {
        return Path.Combine(GetTagDirectory(tag), $"bundle{index:D3}.zonebundle.yml");
    }

    public static string GetBundlePath(string tag, ZoneBundleManifestEntry entry)
    {
        return Path.Combine(GetTagDirectory(tag), entry.File);
    }

    public static bool ArchiveTagExists(string tag)
    {
        return Directory.Exists(GetTagDirectory(tag)) || File.Exists(GetManifestPath(tag));
    }

    public static ZoneBundleManifest LoadManifest(string tag)
    {
        return ZoneBundleSerialization.LoadManifest(GetManifestPath(tag));
    }

    public static void SaveManifest(string manifestPath, ZoneBundleManifest manifest)
    {
        ZoneBundleSerialization.SaveManifest(manifestPath, manifest);
    }

    public static void SaveBundle(string path, ZoneBundleFile bundle)
    {
        ZoneBundleSerialization.SaveBundle(path, bundle);
    }

    public static ZoneBundleFile LoadBundleFromManifestZone(string tag, Vector2i zone)
    {
        return ZoneBundleSerialization.LoadBundle(GetBundlePathFromManifest(tag, zone));
    }

    public static bool TryLoadBundleFromManifestZone(string tag, Vector2i zone, out ZoneBundleFile bundle, out string reason)
    {
        bundle = null!;
        try
        {
            bundle = LoadBundleFromManifestZone(tag, zone);
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static ZoneBundleFile LoadBundleFromManifestEntry(string tag, ZoneBundleManifestEntry entry)
    {
        return ZoneBundleSerialization.LoadBundle(GetBundlePath(tag, entry));
    }

    public static bool TryLoadBundleFromManifestEntry(string tag, ZoneBundleManifestEntry entry, out ZoneBundleFile bundle, out string reason)
    {
        return TryLoadBundleFile(GetBundlePath(tag, entry), out bundle, out reason);
    }

    public static string GetTagDirectory(string tag)
    {
        return Path.Combine(ZoneSaviorPlugin.ZoneBundleStorageFullPath, ZoneSaviorPaths.SanitizePathSegment(tag));
    }

    private static string GetBundlePathFromManifest(string tag, Vector2i zone)
    {
        ZoneBundleManifest manifest = LoadManifest(tag);
        ZoneBundleManifestEntry? entry = manifest.Bundles.FirstOrDefault(candidate =>
        {
            Vector2i candidateZone = ZoneSaviorZones.ToVector2i(candidate.Zone);
            return candidateZone.x == zone.x && candidateZone.y == zone.y;
        });

        if (entry == null)
        {
            throw new FileNotFoundException($"Manifest for tag '{tag}' does not contain source zone ({zone.x},{zone.y}).");
        }

        return GetBundlePath(tag, entry);
    }

    private static bool TryLoadBundleFile(string path, out ZoneBundleFile bundle, out string reason)
    {
        bundle = null!;
        reason = "";
        try
        {
            bundle = ZoneBundleSerialization.LoadBundle(path);
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}

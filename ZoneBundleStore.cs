using System;
using System.Collections.Generic;
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

    public static string GetBundlePath(string tag, string generation, int index)
    {
        string safeGeneration = ZoneSaviorPaths.SanitizePathSegment(generation);
        return Path.Combine(
            GetTagDirectory(tag),
            $"bundle{index:D3}_{safeGeneration}{ZoneBundleSerialization.BundleFileExtension}");
    }

    public static string GetBundlePath(string tag, ZoneBundleManifestEntry entry)
    {
        string fileName = Path.GetFileName(entry.File ?? "");
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, entry.File, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Manifest for tag '{tag}' contains an invalid bundle file path.");
        }

        return Path.Combine(GetTagDirectory(tag), fileName);
    }

    public static bool ArchiveTagExists(string tag)
    {
        return File.Exists(GetManifestPath(tag));
    }

    public static ZoneBundleManifest LoadManifest(string tag)
    {
        ZoneBundleManifest manifest = ZoneBundleSerialization.LoadManifest(GetManifestPath(tag));
        string safeTag = ZoneSaviorPaths.SanitizePathSegment(tag);
        if (!string.Equals(manifest.Tag, safeTag, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Manifest tag '{manifest.Tag}' does not match requested tag '{safeTag}'.");
        }

        return manifest;
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
        return LoadBundleFile(tag, GetBundlePathFromManifest(tag, zone));
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

    public static bool TryLoadBundleFromManifestEntry(string tag, ZoneBundleManifestEntry entry, out ZoneBundleFile bundle, out string reason)
    {
        return TryLoadBundleFile(tag, GetBundlePath(tag, entry), out bundle, out reason);
    }

    public static string GetTagDirectory(string tag)
    {
        string root = Path.GetFullPath(ZoneSaviorPlugin.ZoneBundleStorageFullPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string segment = ZoneSaviorPaths.SanitizePathSegment(tag);
        string directory = Path.GetFullPath(Path.Combine(root, segment));
        string rootPrefix = root + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Tag '{tag}' resolves outside the ZoneSavior bundle directory.");
        }

        return directory;
    }

    public static int CleanupUnreferencedBundles(string tag, ZoneBundleManifest manifest)
    {
        string directory = GetTagDirectory(tag);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        HashSet<string> referenced = manifest.Bundles
            .Select(entry => Path.GetFullPath(GetBundlePath(tag, entry)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int failures = 0;
        foreach (string path in Directory.EnumerateFiles(
                     directory,
                     $"*{ZoneBundleSerialization.BundleFileExtension}",
                     SearchOption.TopDirectoryOnly))
        {
            string fullPath = Path.GetFullPath(path);
            if (referenced.Contains(fullPath))
            {
                continue;
            }

            try
            {
                File.Delete(fullPath);
            }
            catch
            {
                failures++;
            }
        }

        return failures;
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

    private static ZoneBundleFile LoadBundleFile(string tag, string path)
    {
        ZoneBundleFile bundle = ZoneBundleSerialization.LoadBundle(path);
        if (!string.Equals(bundle.Tag, tag, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Bundle tag '{bundle.Tag}' does not match manifest tag '{tag}'.");
        }

        return bundle;
    }

    private static bool TryLoadBundleFile(string tag, string path, out ZoneBundleFile bundle, out string reason)
    {
        bundle = null!;
        reason = "";
        try
        {
            bundle = LoadBundleFile(tag, path);
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}

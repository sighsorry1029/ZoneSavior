using System;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ZoneSavior;

internal static class ZoneBundleSerialization
{
    private const int MaxEntryDataCharacters = 16 * 1024 * 1024;

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .DisableAliases()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new FlowFloatArrayYamlConverter())
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new FlowFloatArrayYamlConverter())
        .Build();

    public static string Serialize<T>(T value)
    {
        return Serializer.Serialize(value);
    }

    public static T Deserialize<T>(string yaml)
    {
        T? value = Deserializer.Deserialize<T>(yaml);
        if (value == null)
        {
            throw new InvalidDataException($"Failed to deserialize {typeof(T).Name}.");
        }

        return value;
    }

    public static void SaveManifest(string path, ZoneBundleManifest manifest)
    {
        ZoneSaviorFiles.WriteAllTextAtomic(path, Serialize(manifest), Encoding.UTF8);
    }

    public static ZoneBundleManifest LoadManifest(string path)
    {
        ZoneBundleManifest manifest = Deserialize<ZoneBundleManifest>(File.ReadAllText(path, Encoding.UTF8));
        if (manifest.Version != ZoneBundleManifest.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported zone bundle manifest version {manifest.Version}.");
        }

        ValidateManifest(manifest);
        return manifest;
    }

    public static void SaveBundle(string path, ZoneBundleFile bundle)
    {
        ZoneSaviorFiles.WriteAllTextAtomic(path, Serialize(bundle), Encoding.UTF8);
    }

    public static ZoneBundleFile LoadBundle(string path)
    {
        ZoneBundleFile bundle = Deserialize<ZoneBundleFile>(File.ReadAllText(path, Encoding.UTF8));
        if (bundle.Version != ZoneBundleFile.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported zone bundle version {bundle.Version}. Re-save this zone with the current ZoneSavior version.");
        }

        ValidateBundle(bundle);
        return bundle;
    }

    private static void ValidateManifest(ZoneBundleManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Tag))
        {
            throw new InvalidDataException("Zone bundle manifest tag is empty.");
        }

        _ = ZoneSaviorPaths.SanitizePathSegment(manifest.Tag);
        if (manifest.SourceRange == null ||
            manifest.SourceRange.MinX > manifest.SourceRange.MaxX ||
            manifest.SourceRange.MinZ > manifest.SourceRange.MaxZ)
        {
            throw new InvalidDataException("Zone bundle manifest source range is invalid.");
        }

        if (manifest.Bundles == null)
        {
            throw new InvalidDataException("Zone bundle manifest bundle list is missing.");
        }

        manifest.SourceZoneCreators ??= [];
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> zones = new(StringComparer.Ordinal);
        foreach (ZoneBundleManifestEntry? entry in manifest.Bundles)
        {
            if (entry?.Zone == null)
            {
                throw new InvalidDataException("Zone bundle manifest contains an entry without a source zone.");
            }

            if (entry.Zone.X < manifest.SourceRange.MinX || entry.Zone.X > manifest.SourceRange.MaxX ||
                entry.Zone.Z < manifest.SourceRange.MinZ || entry.Zone.Z > manifest.SourceRange.MaxZ)
            {
                throw new InvalidDataException($"Manifest zone ({entry.Zone.X},{entry.Zone.Z}) is outside its source range.");
            }

            string fileName = Path.GetFileName(entry.File ?? "");
            if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, entry.File, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Zone bundle manifest contains an invalid bundle file path.");
            }

            if (!files.Add(fileName))
            {
                throw new InvalidDataException($"Zone bundle manifest contains duplicate file '{fileName}'.");
            }

            string zoneKey = $"{entry.Zone.X},{entry.Zone.Z}";
            if (!zones.Add(zoneKey))
            {
                throw new InvalidDataException($"Zone bundle manifest contains duplicate source zone ({entry.Zone.X},{entry.Zone.Z}).");
            }

            entry.SourceZoneCreators ??= [];
        }
    }

    private static void ValidateBundle(ZoneBundleFile bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle.Tag))
        {
            throw new InvalidDataException("Zone bundle tag is empty.");
        }

        _ = ZoneSaviorPaths.SanitizePathSegment(bundle.Tag);
        if (!IsFinite(bundle.SourceBaseY))
        {
            throw new InvalidDataException("Zone bundle source base height is not finite.");
        }

        if (bundle.TerrainContacts == null || bundle.Entries == null)
        {
            throw new InvalidDataException("Zone bundle terrain contacts or entries are missing.");
        }

        if (!bundle.TerrainContactsCaptured && bundle.TerrainContacts.Count > 0)
        {
            throw new InvalidDataException("Zone bundle contains terrain contacts that were not marked as captured.");
        }

        foreach (ZoneBundleTerrainContact? contact in bundle.TerrainContacts)
        {
            if (contact == null ||
                !IsFinite(contact.LocalX) ||
                !IsFinite(contact.LocalZ) ||
                !IsFinite(contact.RelativeY))
            {
                throw new InvalidDataException("Zone bundle contains an invalid terrain contact.");
            }
        }

        for (int index = 0; index < bundle.Entries.Count; index++)
        {
            ZoneBundleEntry? entry = bundle.Entries[index];
            if (entry == null || string.IsNullOrWhiteSpace(entry.Prefab))
            {
                throw new InvalidDataException($"Zone bundle entry {index} has no prefab.");
            }

            ValidateFloatArray(entry.LocalPos, 3, $"entry {index} local position");
            ValidateFloatArray(entry.Rot, 4, $"entry {index} rotation");
            ValidateFloatArray(entry.Scale, 3, $"entry {index} scale");

            entry.Data ??= "";
            if (entry.Data.Length > MaxEntryDataCharacters)
            {
                throw new InvalidDataException($"Zone bundle entry {index} ZDO data is too large.");
            }

            if (!string.IsNullOrEmpty(entry.Data))
            {
                try
                {
                    entry.RuntimeData = new ZoneBundleZdoData(entry.Data);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Zone bundle entry {index} has invalid ZDO data: {ex.Message}", ex);
                }
            }
        }
    }

    private static void ValidateFloatArray(float[]? values, int expectedLength, string label)
    {
        if (values == null || values.Length != expectedLength || values.Any(value => !IsFinite(value)))
        {
            throw new InvalidDataException($"Zone bundle {label} must contain {expectedLength} finite value(s).");
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

internal sealed class FlowFloatArrayYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
    {
        return type == typeof(float[]);
    }

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        parser.Consume<SequenceStart>();
        List<float> values = [];
        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            Scalar scalar = parser.Consume<Scalar>();
            if (!float.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                throw new YamlException(scalar.Start, scalar.End, $"Invalid float value '{scalar.Value}'.");
            }

            values.Add(value);
        }

        return values.ToArray();
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        float[] values = value as float[] ?? [];
        emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Flow));
        foreach (float item in values)
        {
            emitter.Emit(new Scalar(
                AnchorName.Empty,
                TagName.Empty,
                item.ToString("0.###", CultureInfo.InvariantCulture),
                ScalarStyle.Plain,
                isPlainImplicit: true,
                isQuotedImplicit: true));
        }

        emitter.Emit(new SequenceEnd());
    }
}


using System;
using System.IO;
using System.IO.Compression;
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
    public const string BundleFileExtension = ".zonebundle.yml.gz";

    private const int MaxManifestBundleCount = 10000;
    private const int MaxManifestCreatorCount = 100000;
    private const int MaxBundleEntryCount = 100000;
    private const int MaxTerrainContactCount = 100000;
    private const int MaxEntryDataCharacters = 16 * 1024 * 1024;
    private const long MaxManifestBytes = 16L * 1024L * 1024L;
    private const long MaxDecompressedBundleBytes = 128L * 1024L * 1024L;
    private static readonly Encoding BundleEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly ISerializer Serializer = CreateSerializerBuilder().Build();
    private static readonly IDeserializer Deserializer = CreateDeserializerBuilder().Build();
    private static readonly ISerializer BundleSerializer = CreateSerializerBuilder()
        .WithTypeConverter(new CompactZoneBundleEntryYamlConverter())
        .Build();
    private static readonly IDeserializer BundleDeserializer = CreateDeserializerBuilder()
        .WithTypeConverter(new CompactZoneBundleEntryYamlConverter())
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
        if (manifest.Version != ZoneBundleManifest.CurrentVersion)
        {
            string version = manifest.Version?.ToString(CultureInfo.InvariantCulture) ?? "missing";
            throw new InvalidDataException($"Cannot save zone bundle manifest version {version}.");
        }

        ValidateManifest(manifest);
        string yaml = Serialize(manifest);
        if (BundleEncoding.GetByteCount(yaml) > MaxManifestBytes)
        {
            throw new InvalidDataException(
                $"Zone bundle manifest exceeds the {MaxManifestBytes / (1024L * 1024L)} MiB limit.");
        }

        ZoneSaviorFiles.WriteAllTextAtomic(path, yaml, BundleEncoding);
    }

    public static ZoneBundleManifest LoadManifest(string path)
    {
        if (new FileInfo(path).Length > MaxManifestBytes)
        {
            throw new InvalidDataException(
                $"Zone bundle manifest exceeds the {MaxManifestBytes / (1024L * 1024L)} MiB limit.");
        }

        ZoneBundleManifest manifest = Deserialize<ZoneBundleManifest>(File.ReadAllText(path, BundleEncoding));
        if (manifest.Version != ZoneBundleManifest.CurrentVersion)
        {
            string version = manifest.Version?.ToString(CultureInfo.InvariantCulture) ?? "missing";
            throw new InvalidDataException(
                $"Unsupported zone bundle manifest version {version}. Legacy archives are not converted; create a new archive from the live world with the current ZoneSavior version.");
        }

        ValidateManifest(manifest);
        return manifest;
    }

    public static void SaveBundle(string path, ZoneBundleFile bundle)
    {
        RequireCompressedBundlePath(path);
        if (bundle.Version != ZoneBundleFile.CurrentVersion)
        {
            string version = bundle.Version?.ToString(CultureInfo.InvariantCulture) ?? "missing";
            throw new InvalidDataException($"Cannot save zone bundle version {version}.");
        }

        ValidateBundle(bundle);
        ZoneSaviorFiles.WriteAtomic(path, stream =>
        {
            using GZipStream gzip = new(stream, CompressionLevel.Optimal, leaveOpen: true);
            using SizeLimitedStream limited = new(gzip, MaxDecompressedBundleBytes);
            using StreamWriter writer = new(limited, BundleEncoding, 4096, leaveOpen: true);
            BundleSerializer.Serialize(writer, bundle);
            writer.Flush();
        });
    }

    public static ZoneBundleFile LoadBundle(string path)
    {
        RequireCompressedBundlePath(path);
        ZoneBundleFile? bundle;
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (GZipStream gzip = new(stream, CompressionMode.Decompress))
        using (SizeLimitedStream limited = new(gzip, MaxDecompressedBundleBytes))
        using (StreamReader reader = new(limited, BundleEncoding, detectEncodingFromByteOrderMarks: true))
        {
            bundle = BundleDeserializer.Deserialize<ZoneBundleFile>(reader);
        }

        if (bundle == null)
        {
            throw new InvalidDataException("Failed to deserialize ZoneBundleFile.");
        }

        if (bundle.Version != ZoneBundleFile.CurrentVersion)
        {
            string version = bundle.Version?.ToString(CultureInfo.InvariantCulture) ?? "missing";
            throw new InvalidDataException(
                $"Unsupported zone bundle version {version}. Legacy bundles are not converted; create a new archive from the live world with the current ZoneSavior version.");
        }

        ValidateBundle(bundle);
        return bundle;
    }

    private static SerializerBuilder CreateSerializerBuilder()
    {
        return new SerializerBuilder()
            .DisableAliases()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new FlowFloatArrayYamlConverter());
    }

    private static DeserializerBuilder CreateDeserializerBuilder()
    {
        return new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new FlowFloatArrayYamlConverter());
    }

    private static void RequireCompressedBundlePath(string path)
    {
        if (!path.EndsWith(BundleFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Unsupported zone bundle file '{Path.GetFileName(path)}'. Expected '*{BundleFileExtension}'. Legacy bundles are not converted.");
        }
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
        if (manifest.Bundles.Count > MaxManifestBundleCount)
        {
            throw new InvalidDataException(
                $"Zone bundle manifest contains {manifest.Bundles.Count} bundles; the maximum is {MaxManifestBundleCount}.");
        }

        long creatorCount = manifest.SourceZoneCreators.Count;
        if (creatorCount > MaxManifestCreatorCount)
        {
            throw new InvalidDataException(
                $"Zone bundle manifest contains more than {MaxManifestCreatorCount} creator records.");
        }

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

            if (!fileName.EndsWith(BundleFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Zone bundle manifest references unsupported file '{fileName}'. Expected '*{BundleFileExtension}'.");
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
            creatorCount += entry.SourceZoneCreators.Count;
            if (creatorCount > MaxManifestCreatorCount)
            {
                throw new InvalidDataException(
                    $"Zone bundle manifest contains more than {MaxManifestCreatorCount} creator records.");
            }
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

        if (bundle.TerrainContacts.Count > MaxTerrainContactCount)
        {
            throw new InvalidDataException(
                $"Zone bundle contains {bundle.TerrainContacts.Count} terrain contacts; the maximum is {MaxTerrainContactCount}.");
        }

        if (bundle.Entries.Count > MaxBundleEntryCount)
        {
            throw new InvalidDataException(
                $"Zone bundle contains {bundle.Entries.Count} entries; the maximum is {MaxBundleEntryCount}.");
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

    private sealed class SizeLimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _processedBytes;

        public SizeLimitedStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            AddProcessedBytes(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            AddProcessedBytes(count);
            _inner.Write(buffer, offset, count);
        }

        private void AddProcessedBytes(int count)
        {
            if (count > _maxBytes - _processedBytes)
            {
                throw new InvalidDataException(
                    $"Zone bundle decompressed data exceeds the {MaxDecompressedBundleBytes / (1024L * 1024L)} MiB limit.");
            }

            _processedBytes += count;
        }
    }
}

internal sealed class CompactZoneBundleEntryYamlConverter : IYamlTypeConverter
{
    private const char FieldSeparator = ';';
    private const char ValueSeparator = ',';

    public bool Accepts(Type type)
    {
        return type == typeof(ZoneBundleEntry);
    }

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        Scalar scalar = parser.Consume<Scalar>();
        string[] fields = SplitFields(scalar);
        return new ZoneBundleEntry
        {
            Prefab = fields[0].Trim(),
            LocalPos = ParseFloatArray(fields[1], 3, "local position", scalar),
            Rot = ParseFloatArray(fields[2], 4, "rotation", scalar),
            Scale = ParseFloatArray(fields[3], 3, "scale", scalar),
            Data = fields[4].Trim()
        };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is not ZoneBundleEntry entry)
        {
            throw new InvalidDataException("Cannot serialize a null zone bundle entry.");
        }

        if (entry.Prefab.IndexOf(FieldSeparator) >= 0)
        {
            throw new InvalidDataException(
                $"Zone bundle prefab '{entry.Prefab}' contains the reserved '{FieldSeparator}' delimiter.");
        }

        StringBuilder row = new();
        row.Append(entry.Prefab);
        row.Append(FieldSeparator);
        AppendFloatArray(row, entry.LocalPos);
        row.Append(FieldSeparator);
        AppendFloatArray(row, entry.Rot);
        row.Append(FieldSeparator);
        AppendFloatArray(row, entry.Scale);
        row.Append(FieldSeparator);
        row.Append(entry.Data ?? "");

        emitter.Emit(new Scalar(
            AnchorName.Empty,
            TagName.Empty,
            row.ToString(),
            ScalarStyle.SingleQuoted,
            isPlainImplicit: true,
            isQuotedImplicit: true));
    }

    private static string[] SplitFields(Scalar scalar)
    {
        string value = scalar.Value;
        string[] fields = new string[5];
        int start = 0;
        for (int index = 0; index < fields.Length - 1; index++)
        {
            int separator = value.IndexOf(FieldSeparator, start);
            if (separator < 0)
            {
                throw new YamlException(
                    scalar.Start,
                    scalar.End,
                    $"Compact zone bundle entry must contain five '{FieldSeparator}'-separated fields.");
            }

            fields[index] = value.Substring(start, separator - start);
            start = separator + 1;
        }

        fields[fields.Length - 1] = value.Substring(start);
        return fields;
    }

    private static float[] ParseFloatArray(string field, int expectedLength, string label, Scalar scalar)
    {
        string[] values = field.Split(ValueSeparator);
        if (values.Length != expectedLength)
        {
            throw new YamlException(
                scalar.Start,
                scalar.End,
                $"Compact zone bundle {label} must contain {expectedLength} values.");
        }

        float[] result = new float[expectedLength];
        for (int index = 0; index < values.Length; index++)
        {
            if (!float.TryParse(values[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
                float.IsNaN(value) ||
                float.IsInfinity(value))
            {
                throw new YamlException(
                    scalar.Start,
                    scalar.End,
                    $"Compact zone bundle {label} contains invalid value '{values[index]}'.");
            }

            result[index] = value;
        }

        return result;
    }

    private static void AppendFloatArray(StringBuilder row, IReadOnlyList<float> values)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                row.Append(ValueSeparator);
            }

            row.Append(values[index].ToString("0.###", CultureInfo.InvariantCulture));
        }
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


using System;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ZoneSavior;

internal static class ZoneBundleSerialization
{
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(manifest), Encoding.UTF8);
    }

    public static ZoneBundleManifest LoadManifest(string path)
    {
        return Deserialize<ZoneBundleManifest>(File.ReadAllText(path, Encoding.UTF8));
    }

    public static void SaveBundle(string path, ZoneBundleFile bundle)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(bundle), Encoding.UTF8);
    }

    public static ZoneBundleFile LoadBundle(string path)
    {
        return Deserialize<ZoneBundleFile>(File.ReadAllText(path, Encoding.UTF8));
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


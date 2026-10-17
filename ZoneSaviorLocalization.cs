using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using YamlDotNet.Serialization;

namespace ZoneSavior;

internal static class ZoneSaviorLocalization
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();
    private static readonly Dictionary<string, Dictionary<string, string>> LoadedTranslations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> EmbeddedEnglish = new(StringComparer.Ordinal);
    private static readonly HashSet<string> InvalidFormatsLogged = new(StringComparer.Ordinal);

    // Original Valheim 1.0.20 exposes no non-initializing instance getter or public AddWord.
    // Resolve the private contracts once; never initialize Unity localization on a dedicated server.
    private static readonly AccessTools.FieldRef<Localization> CurrentLocalization =
        AccessTools.StaticFieldRefAccess<Localization>(AccessTools.Field(typeof(Localization), "m_instance"));
    private static readonly AccessTools.FieldRef<Localization, Dictionary<string, string>> Words =
        AccessTools.FieldRefAccess<Localization, Dictionary<string, string>>("m_translations");
    private static readonly AccessTools.FieldRef<Localization, LRUCache<string>> Cache =
        AccessTools.FieldRefAccess<Localization, LRUCache<string>>("m_cache");

    private static ManualLogSource? _logger;
    private static bool _loaded;
    private static string _language = "English";

    public static int Revision { get; private set; }

    public static void Load(ManualLogSource logger)
    {
        _logger = logger;
        if (_loaded) return;
        _loaded = true;

        AddEmbeddedYaml("English");
        AddEmbeddedYaml("Korean");
        AddExternalYamlFiles();
        Localization? localization = CurrentLocalization();
        if (localization != null) Apply(localization, localization.GetSelectedLanguage());
    }

    public static string Text(string key)
    {
        key = NormalizeKey(key);
        if (TryGetText(_language, key, out string text) || TryGetText("English", key, out text)) return text;
        return "$" + key;
    }

    public static string Format(string key, params object[] args)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, Text(key), args);
        }
        catch (FormatException)
        {
            key = NormalizeKey(key);
            if (InvalidFormatsLogged.Add(_language + ":" + key))
                _logger?.LogWarning($"Invalid ZoneSavior localization format for '{key}' ({_language}); using embedded English.");

            // External English is also editable, so retain the pristine embedded fallback.
            if (EmbeddedEnglish.TryGetValue(key, out string fallback))
            {
                try { return string.Format(CultureInfo.InvariantCulture, fallback, args); }
                catch (FormatException) { }
            }
            return "[" + key + "]";
        }
    }

    public static string MaybeLocalize(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('$') < 0) return value;
        Localization? localization = CurrentLocalization();
        if (localization != null) return localization.Localize(value);
        return value.StartsWith("$zs_", StringComparison.Ordinal) ? Text(value) : value;
    }

    private static string NormalizeKey(string key) => key.TrimStart('$');

    private static bool TryGetText(string language, string key, out string text)
    {
        text = "";
        return LoadedTranslations.TryGetValue(language, out Dictionary<string, string> translations) &&
               translations.TryGetValue(key, out text);
    }

    private static void AddEmbeddedYaml(string language)
    {
        Assembly assembly = typeof(ZoneSaviorLocalization).Assembly;
        string suffix = ".translations." + language + ".yml";
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using Stream? stream = resource == null ? null : assembly.GetManifestResourceStream(resource);
        if (stream == null)
        {
            _logger?.LogWarning($"ZoneSavior localization resource not found: translations/{language}.yml");
            return;
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        AddYamlTranslations(language, reader.ReadToEnd(), $"embedded translations/{language}.yml");
        if (language.Equals("English", StringComparison.OrdinalIgnoreCase) && LoadedTranslations.TryGetValue(language, out var english))
            foreach (var pair in english) EmbeddedEnglish[pair.Key] = pair.Value;
    }

    private static void AddExternalYamlFiles()
    {
        string directory = Paths.PluginPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory, "ZoneSavior.*.yml", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(directory, "ZoneSavior.*.yaml", SearchOption.AllDirectories))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to find external ZoneSavior localization files: {ex.Message}");
            return;
        }

        HashSet<string> loadedLanguages = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            if (!TryGetExternalLanguage(file, out string language)) continue;
            if (loadedLanguages.Contains(language))
            {
                _logger?.LogWarning($"Duplicate external ZoneSavior localization for '{language}' skipped: {file}");
                continue;
            }

            try
            {
                if (!AddYamlTranslations(language, File.ReadAllText(file, Encoding.UTF8), file)) continue;
                loadedLanguages.Add(language);
                _logger?.LogInfo($"Loaded external ZoneSavior localization '{language}' from {file}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to load external ZoneSavior localization '{file}': {ex.Message}");
            }
        }
    }

    private static bool TryGetExternalLanguage(string file, out string language)
    {
        const string prefix = "ZoneSavior.";
        string name = Path.GetFileNameWithoutExtension(file);
        language = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name.Substring(prefix.Length).Trim() : "";
        return language.Length > 0;
    }

    private static bool AddYamlTranslations(string language, string yaml, string source)
    {
        // Deserialize and validate before merging, so malformed files cannot partially replace a language.
        Dictionary<string, string>? parsed = Deserializer.Deserialize<Dictionary<string, string>>(yaml);
        Dictionary<string, string> valid = new(StringComparer.Ordinal);
        if (parsed != null)
        {
            foreach (var pair in parsed)
            {
                string key = NormalizeKey(pair.Key);
                if (key.StartsWith("zs_", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(pair.Value))
                    valid[key] = pair.Value;
            }
        }
        if (valid.Count == 0)
        {
            _logger?.LogWarning($"ZoneSavior localization '{source}' did not contain any valid translations.");
            return false;
        }

        if (!LoadedTranslations.TryGetValue(language, out var loaded))
            LoadedTranslations[language] = loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in valid) loaded[pair.Key] = pair.Value;
        return true;
    }

    private static void Apply(Localization localization, string language)
    {
        Dictionary<string, string> words = Words(localization);
        // SetupLanguage can be called without Clear. Remove our previous language's extra keys as well.
        foreach (var translations in LoadedTranslations.Values)
            foreach (string key in translations.Keys) words.Remove(key);
        if (LoadedTranslations.TryGetValue("English", out var english))
            foreach (var pair in english) words[pair.Key] = pair.Value;
        if (LoadedTranslations.TryGetValue(language, out var selected))
            foreach (var pair in selected) words[pair.Key] = pair.Value;
        foreach (string name in LoadedTranslations.Keys)
            if (!localization.GetLanguages().Contains(name)) localization.GetLanguages().Add(name);

        // Localize caches formatted strings; replacing the word dictionary alone leaves stale text.
        Cache(localization).EvictAll();
        _language = language;
        unchecked { Revision++; }
    }

    [HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
    private static class LanguagePatch
    {
        private static void Postfix(Localization __instance, string language)
        {
            if (_loaded) Apply(__instance, language);
        }
    }
}

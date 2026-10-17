using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;

namespace ZoneSavior.Tests;

// Pure managed checks run inside the existing disposable Unity Mono host.
// No Player construction, plugin Awake, native detour, world or network is started.
internal static class BuildRulesRegression
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;

    public static void Run(Assembly mod)
    {
        CheckRules(mod);
        CheckBuildGuard(mod);
        CheckLocalization(mod);
        System.Console.WriteLine("PASS " + checks + " build-rule, placement-IL and localization regression checks.");
    }

    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception(description);
        checks++;
    }

    private static object Call(Type type, string method, params object[] args) => type.GetMethod(method, Static).Invoke(null, args);
    private static object Property(object value, string name) => value.GetType().GetProperty(name).GetValue(value);

    private static void CheckRules(Assembly mod)
    {
        Type configuration = mod.GetType("ZoneSavior.ZoneLimitConfiguration", true);
        Type general = mod.GetType("ZoneSavior.GeneralConfig", true);
        FieldInfo settingField = general.GetField("_zoneWearNTearLimit", Static);
        object previousSetting = settingField.GetValue(null);
        Type toggle = settingField.FieldType.GetGenericArguments()[0];
        // Only supply the value read by the production facade; avoid ConfigFile's global
        // initializer, whose Paths/config environment belongs to an actual BepInEx launch.
        object entry = FormatterServices.GetUninitializedObject(settingField.FieldType);
        AccessTools.Field(settingField.FieldType, "_typedValue").SetValue(entry, Enum.Parse(toggle, "Off"));
        settingField.SetValue(null, entry);
        try
        {
            object[] Parse(string yaml, bool success)
            {
                object[] args = { yaml, null, null, null };
                bool result = (bool)Call(configuration, "TryParseYaml", args);
                Check(result == success, "YAML acceptance: " + yaml);
                Check(success || !string.IsNullOrWhiteSpace((string)args[3]), "Rejected YAML supplies a reason");
                return args;
            }
            bool Blocks(object rule, string prefab) => (bool)rule.GetType().GetMethod("IsPieceBlocked").Invoke(rule, new object[] { prefab });
            string Rule(string extra) => "rules:\n  - name: center\n    x: [-1, 1]\n    z: [-1, 1]\n    limit: 50\n" + extra;

            IList old = (IList)Parse(Rule("    count_creatorless: true\n"), true)[1];
            Check(old.Count == 1 && (int)Property(old[0], "Limit") == 50 && (bool)Property(old[0], "CountCreatorless"), "Existing YAML keeps quantity and creatorless semantics");
            Check(!Blocks(old[0], "portal_wood"), "Omitted blocked_pieces does not restrict pieces");
            foreach (string list in new[] { "[]", "null" })
                Check(!Blocks(((IList)Parse(Rule("    blocked_pieces: " + list + "\n"), true)[1])[0], "portal_wood"), "Empty or null list is unrestricted");

            IList named = (IList)Parse(Rule("    blocked_pieces: [portal_wood, ' piece_workbench ', portal_wood]\n"), true)[1];
            Check(Blocks(named[0], "portal_wood") && Blocks(named[0], "piece_workbench"), "Trim names and accept duplicates");
            Check(!Blocks(named[0], "Portal_Wood") && !Blocks(named[0], "portal_wood(Clone)") && !Blocks(named[0], "piece_workbench_extra"), "Prefab policy is exact and case-sensitive");
            Check(!Blocks(named[0], null) && !Blocks(named[0], ""), "Null or empty candidate never matches");
            IList reversed = (IList)Parse("defaults:\n  count_creatorless: true\nrules:\n  - x: [2, -2]\n    z: [3, -3]\n    limit: 0\n", true)[1];
            Check((int)Property(reversed[0], "MinX") == -2 && (int)Property(reversed[0], "MaxX") == 2 &&
                (int)Property(reversed[0], "MinZ") == -3 && (int)Property(reversed[0], "MaxZ") == 3 &&
                (int)Property(reversed[0], "Limit") == 0 && (bool)Property(reversed[0], "CountCreatorless"), "Reversed ranges, zero limit and default creatorless policy preserved");

            string overlap = Rule("    blocked_pieces: [portal_wood]\n") +
                "  - name: outer\n    x: [-2, 2]\n    z: [-2, 2]\n    limit: 100\n    blocked_pieces: [piece_workbench]\n";
            Call(configuration, "ApplyYaml", overlap, "managed test");
            Check(!(bool)configuration.GetProperty("Enabled", Static).GetValue(null), "Quantity toggle is off in isolated test");
            Check((bool)configuration.GetProperty("HasBlockedPieces", Static).GetValue(null), "Block lists remain active with quantity toggle off");
            object Find(int x, int z, bool expected)
            {
                object[] args = { new Vector2i(x, z), null };
                Check((bool)Call(configuration, "TryGetRule", args) == expected, "Rule coverage at " + x + "," + z);
                return args[1];
            }
            foreach (int x in new[] { -1, 0, 1 })
            foreach (int z in new[] { -1, 0, 1 })
            {
                object rule = Find(x, z, true);
                Check((string)Property(rule, "Name") == "center" && Blocks(rule, "portal_wood") && !Blocks(rule, "piece_workbench"), "First match wins without unioning overlapping bans");
            }
            Check(Blocks(Find(2, -2, true), "piece_workbench"), "Outer edge applies outer policy");
            Find(3, 0, false);
            Find(0, -3, false);

            string[] invalid = {
                "", "rules: [", Rule("    blocked_pieces: ['']\n"), Rule("    blocked_pieces: ['   ']\n"),
                Rule("    blocked_pieces: [null]\n"), Rule("    blocked_pieces: portal_wood\n"),
                Rule("    blocked_pieces: { portal_wood: true }\n"), Rule("").Replace("    limit: 50\n", ""),
                Rule("").Replace("limit: 50", "limit: -1"), Rule("").Replace("x: [-1, 1]", "x: [-1]"),
                Rule("").Replace("z: [-1, 1]", "z: [0, 1, 2]"), "rules: [null]"
            };
            foreach (string yaml in invalid)
            {
                Parse(yaml, false);
                Call(configuration, "ApplyYaml", yaml, "invalid managed test");
                object retained = Find(0, 0, true);
                Check((string)Property(retained, "Name") == "center" && Blocks(retained, "portal_wood") &&
                    (bool)configuration.GetProperty("HasBlockedPieces", Static).GetValue(null), "Invalid reload retains the previous live policy");
            }
            Call(configuration, "ApplyYaml", Rule(""), "managed test without bans");
            Check(!(bool)configuration.GetProperty("HasBlockedPieces", Static).GetValue(null), "Reload clears prior bans when omitted");
            Call(configuration, "ApplyYaml", "rules: []\n", "empty managed test");
            Find(0, 0, false);
            Check(!(bool)configuration.GetProperty("HasBlockedPieces", Static).GetValue(null), "Empty rules clear the aggregate ban state");
        }
        finally
        {
            Call(configuration, "ApplyYaml", "rules: []\n", "test cleanup");
            settingField.SetValue(null, previousSetting);
        }
    }

    private static void CheckBuildGuard(Assembly mod)
    {
        Type guard = mod.GetType("ZoneSavior.ZoneBuildRestrictions", true);
        MethodInfo canPlace = guard.GetMethod("CanPlace", Static);
        Check(canPlace != null && canPlace.ReturnType == typeof(bool) &&
            canPlace.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Player), typeof(Piece) }), "Placement guard contract");
        MethodInfo transpiler = mod.GetTypes().Where(t => t.FullName.Contains("ZoneBuildRestrictions"))
            .SelectMany(t => t.GetMethods(Static)).Single(m => m.Name == "Transpiler");
        MethodInfo target = AccessTools.DeclaredMethod(typeof(Player), "TryPlacePiece", new[] { typeof(Piece) });
        MethodInfo update = AccessTools.DeclaredMethod(typeof(Player), "UpdatePlacementGhost", new[] { typeof(bool) });
        var original = PatchProcessor.GetOriginalInstructions(target, out ILGenerator generator);
        Check(original.Count(c => Equals(c.operand, update)) == 1, "Original game has one final placement-ghost update");
        List<CodeInstruction> Transform(List<CodeInstruction> instructions)
        {
            object[] args = transpiler.GetParameters().Select(p =>
                p.ParameterType == typeof(ILGenerator) ? (object)generator :
                typeof(MethodBase).IsAssignableFrom(p.ParameterType) ? target :
                typeof(IEnumerable<CodeInstruction>).IsAssignableFrom(p.ParameterType) ? instructions :
                throw new InvalidOperationException("Unexpected transpiler parameter " + p)).ToArray();
            return ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, args)).ToList();
        }
        var rewritten = Transform(original);
        int anchor = rewritten.FindIndex(c => Equals(c.operand, update));
        int check = rewritten.FindIndex(c => Equals(c.operand, canPlace));
        Check(rewritten.Count(c => Equals(c.operand, canPlace)) == 1 && check == anchor + 3, "Guard runs once immediately after final ghost update");
        Check(rewritten[check - 2].opcode == OpCodes.Ldarg_0 && rewritten[check - 1].opcode == OpCodes.Ldarg_1, "Guard receives placing Player and selected Piece");
        Check((rewritten[check + 1].opcode == OpCodes.Brtrue || rewritten[check + 1].opcode == OpCodes.Brtrue_S) &&
            rewritten[check + 2].opcode == OpCodes.Ldc_I4_0 && rewritten[check + 3].opcode == OpCodes.Ret, "Denied placement returns false before game creation and resource-consumption success path");
        Check(rewritten[check + 4].labels.Contains((Label)rewritten[check + 1].operand), "Accepted placement resumes original code");
        Check(rewritten.FindIndex(c => c.operand is MethodInfo m && m.DeclaringType == typeof(Player) && m.Name == "PlacePiece") > check, "Object creation remains after the guard");
        var retained = rewritten.Take(anchor + 1).Concat(rewritten.Skip(check + 4)).ToArray();
        Check(retained.Length == original.Count && retained.Zip(original, (a, b) => a.opcode == b.opcode && Equals(a.operand, b.operand)).All(equal => equal), "Transpiler preserves every existing instruction and operand");
        foreach (var changed in new[] {
            new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Ret) },
            new List<CodeInstruction> { new CodeInstruction(OpCodes.Call, update), new CodeInstruction(OpCodes.Call, update), new CodeInstruction(OpCodes.Ret) }
        })
        {
            bool failed = false;
            try { Transform(changed); }
            catch (Exception ex) { failed = (ex is TargetInvocationException tie ? tie.InnerException : ex) is InvalidOperationException; }
            Check(failed, "Missing or ambiguous anchor fails explicitly instead of silently bypassing restrictions");
        }
    }

    private static void CheckLocalization(Assembly mod)
    {
        Type loader = mod.GetType("ZoneSavior.ZoneSaviorLocalization", true);
        FieldInfo language = loader.GetField("_language", Static);
        FieldInfo instance = AccessTools.Field(typeof(Localization), "m_instance");
        object oldInstance = instance.GetValue(null);
        object oldLanguage = language.GetValue(null);
        instance.SetValue(null, null);
        string Text(string key) => (string)Call(loader, "Text", key);
        string Format(string key, params object[] args) => (string)Call(loader, "Format", key, args);
        bool Add(string locale, string yaml) => (bool)Call(loader, "AddYamlTranslations", locale, yaml, "managed test");
        try
        {
            foreach (string locale in new[] { "English", "Korean" })
            {
                Check(mod.GetManifestResourceNames().Count(n => n.EndsWith(".translations." + locale + ".yml", StringComparison.Ordinal)) == 1,
                    "Final merged DLL contains exactly one embedded " + locale + " translation");
                Call(loader, "AddEmbeddedYaml", locale);
            }
            language.SetValue(null, "English");
            string english = Format("zs_build_limit_reached", 50);
            Check(english == "You can place up to 50 building pieces in this zone.", "English quantity message states inclusive maximum without internal component names");
            Check(Text("$zs_zone_coordinates") == Text("zs_zone_coordinates"), "Keys accept game-token prefix");
            Check(Text("zs_unknown_test_key") == "$zs_unknown_test_key", "Unknown key remains diagnosable");
            language.SetValue(null, "Korean");
            Check(Format("zs_build_limit_reached", 50) == "이 구역에는 건축물을 최대 50개까지 설치할 수 있습니다.", "Korean quantity message states inclusive maximum");
            Check(Format("zs_build_piece_blocked", "작업대").Contains("작업대"), "Blocked-piece message inserts localized piece display name");
            Check(Format("zs_zone_coordinates", -173, 104) == "구역 -173, 104", "Coordinate formatting preserves signs and arguments");
            Check((string)Call(loader, "MaybeLocalize", "Modded workbench") == "Modded workbench" &&
                (string)Call(loader, "MaybeLocalize", "$piece_workbench") == "$piece_workbench", "Headless fallback preserves literal names and unresolved game keys");
            Check((string)Call(loader, "MaybeLocalize", "$zs_zone_coordinates") == Text("zs_zone_coordinates"), "Own keys resolve without creating game localization");
            Check(instance.GetValue(null) == null, "Managed fallback never creates Localization.instance on a headless server");

            Check(Add("TestLanguage", "zs_build_piece_blocked: 'Custom {0}'\nzs_language_only: 'Only this language'\n"), "External partial language accepted");
            language.SetValue(null, "testlanguage");
            Check(Format("zs_build_piece_blocked", "Desk") == "Custom Desk" && Format("zs_build_limit_reached", 50) == english,
                "Language names are case-insensitive and missing translations fall back to English");
            Check(!Add("TestLanguage", "menu_quit: 'do not override game text'\nzs_empty: ''\n"), "Unrelated game keys and empty values do not replace translations");
            Check(Text("menu_quit") == "$menu_quit", "Only ZoneSavior-owned keys enter the translation map");
            bool malformed = false;
            try { Add("TestLanguage", "zs_build_piece_blocked: 'Should not apply'\nzs_broken: [\n"); }
            catch (TargetInvocationException) { malformed = true; }
            Check(malformed && Format("zs_build_piece_blocked", "Desk") == "Custom Desk", "Malformed file leaves the entire previous language intact");

            // Exercise original private fields through the production accessors without Unity initialization.
            var localization = (Localization)FormatterServices.GetUninitializedObject(typeof(Localization));
            var words = new Dictionary<string, string> { ["piece_workbench"] = "Workbench" };
            var cache = new LRUCache<string>(4);
            cache.Put("$zs_build_piece_blocked", "Stale language");
            AccessTools.Field(typeof(Localization), "m_translations").SetValue(localization, words);
            AccessTools.Field(typeof(Localization), "m_languages").SetValue(localization, new List<string> { "English" });
            AccessTools.Field(typeof(Localization), "m_cache").SetValue(localization, cache);
            int revision = (int)loader.GetProperty("Revision", Static).GetValue(null);
            Call(loader, "Apply", localization, "TestLanguage");
            Check(words["zs_build_piece_blocked"] == "Custom {0}" && words["zs_build_limit_reached"].Contains("{0}") &&
                words["piece_workbench"] == "Workbench", "Apply overlays selected language and English fallback without altering game words");
            Check(!cache.TryGet("$zs_build_piece_blocked", out _) &&
                (int)loader.GetProperty("Revision", Static).GetValue(null) == revision + 1, "Language change invalidates game cache and HUD revision");
            Call(loader, "Apply", localization, "English");
            Check(!words.ContainsKey("zs_language_only"), "Switching languages removes previous language-only keys");
            Check(localization.GetLanguages().Count(n => n == "TestLanguage") == 1, "Repeated language application does not duplicate language entries");

            Add("Korean", "zs_build_limit_reached: '{9}'\n");
            Add("English", "zs_build_limit_reached: '{invalid}'\n");
            language.SetValue(null, "Korean");
            Check(Format("zs_build_limit_reached", 50) == english, "Broken selected and external English formats fall back to pristine embedded English");
            Check(Format("zs_zone_coordinates", 1) == "[zs_zone_coordinates]", "Insufficient caller arguments fail safely without throwing during placement");
        }
        finally
        {
            // Keep other compatibility fixtures independent from our override scenarios.
            ((IDictionary)loader.GetField("LoadedTranslations", Static).GetValue(null)).Clear();
            Call(loader, "AddEmbeddedYaml", "English");
            Call(loader, "AddEmbeddedYaml", "Korean");
            language.SetValue(null, oldLanguage);
            instance.SetValue(null, oldInstance);
        }
    }
}

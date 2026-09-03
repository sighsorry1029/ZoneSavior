using BepInEx.Configuration;
using UnityEngine;

namespace ZoneSavior;

internal sealed class ConfigurationManagerAttributes
{
    public int Order { get; set; }
}

internal static class ConfigSections
{
    public const string General = "01 - General";
    public const string ZoneSavior = "02 - ZoneSavior";
    public const string AutoArchive = "03 - Auto Archive";
}

internal static class ConfigDescriptions
{
    public static ConfigDescription Ordered(string description, int order)
    {
        return new ConfigDescription(description, null, new ConfigurationManagerAttributes { Order = order });
    }

    public static ConfigDescription Ordered(string description, AcceptableValueBase acceptableValues, int order)
    {
        return new ConfigDescription(description, acceptableValues, new ConfigurationManagerAttributes { Order = order });
    }
}

internal static class GeneralConfig
{
    private static ConfigEntry<ZoneSaviorPlugin.Toggle> _serverConfigLocked = null!;
    private static ConfigEntry<ZoneSaviorPlugin.Toggle> _zoneWearNTearLimit = null!;

    public static bool ZoneWearNTearLimitEnabled => _zoneWearNTearLimit.Value == ZoneSaviorPlugin.Toggle.On;

    public static void Bind(ZoneSaviorPlugin plugin)
    {
        _serverConfigLocked = plugin.config(ConfigSections.General, "Lock Configuration", ZoneSaviorPlugin.Toggle.On, "If on, the server controls synced settings.");
        _ = ZoneSaviorPlugin.ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
        _zoneWearNTearLimit = plugin.config(
            ConfigSections.ZoneSavior,
            "Zone WearNTear Limit",
            ZoneSaviorPlugin.Toggle.Off,
            ConfigDescriptions.Ordered(
                "If on, ZoneSavior enforces per-zone WearNTear limits from BepInEx/config/ZoneSavior/zones.yml. If off, zone rules stay loaded but placement is not rejected by zone count.",
                590));
        _zoneWearNTearLimit.SettingChanged += (_, _) => ZonePieceCounter.RebuildCounts();
    }
}

internal static class ClientConfig
{
    private static ConfigEntry<float> _counterVisibleSeconds = null!;
    private static ConfigEntry<KeyboardShortcut> _zoneUiToggleHotkey = null!;

    public static float CounterVisibleSeconds => Mathf.Max(0.1f, _counterVisibleSeconds.Value);
    public static KeyboardShortcut ZoneUiToggleHotkey => _zoneUiToggleHotkey.Value;

    public static void Bind(ZoneSaviorPlugin plugin)
    {
        _counterVisibleSeconds = plugin.config(
            ConfigSections.ZoneSavior,
            "Build Counter Visible Seconds",
            2.5f,
            ConfigDescriptions.Ordered(
                "How long the top build counter stays visible after you place a build piece.",
                new AcceptableValueRange<float>(0.1f, 10f),
                570),
            synchronizedSetting: false);
        _zoneUiToggleHotkey = plugin.config(
            ConfigSections.ZoneSavior,
            "Zone UI Toggle Hotkey",
            new KeyboardShortcut(KeyCode.F8),
            ConfigDescriptions.Ordered(
                "Client-only hotkey that toggles the current zone number HUD and floor boundary line. The Zone UI starts hidden after login.",
                580),
            synchronizedSetting: false);
    }
}

internal enum ZoneBundleWearNTearSaveMode
{
    CreatorOnly = 0,
    IncludeCreatorless = 1
}

internal static class ZoneBundleConfig
{
    private static ConfigEntry<ZoneBundleWearNTearSaveMode> _wearNTearSaveMode = null!;
    private static ConfigEntry<float> _supportFillFeatherWidth = null!;
    private static ConfigEntry<float> _supportFillContactTolerance = null!;
    private static ConfigEntry<float> _supportGraceMinutes = null!;

    public static ZoneBundleWearNTearSaveMode WearNTearSaveMode => _wearNTearSaveMode.Value;
    public static float SupportFillFeatherWidth => Mathf.Clamp(_supportFillFeatherWidth.Value, 0f, 64f);
    public static float SupportFillContactTolerance => Mathf.Clamp(_supportFillContactTolerance.Value, 0.01f, 2f);
    public static float SupportGraceMinutes => Mathf.Clamp(_supportGraceMinutes.Value, 0f, 120f);

    public static void Bind(ZoneSaviorPlugin plugin)
    {
        _wearNTearSaveMode = plugin.config(
            ConfigSections.ZoneSavior,
            "WearNTear Save Mode",
            ZoneBundleWearNTearSaveMode.CreatorOnly,
            ConfigDescriptions.Ordered(
                "Controls which WearNTear objects SupportFill saves. CreatorOnly saves only player-created WearNTear. IncludeCreatorless also saves WearNTear with no creator id.",
                600));
        _supportGraceMinutes = plugin.config(
            ConfigSections.ZoneSavior,
            "Zone Bundle Support Grace Minutes",
            60f,
            ConfigDescriptions.Ordered(
                "Temporary runtime-only WearNTear support grace applied to target zones after zs_loadzone. Set to 0 to disable. This is not saved across server restarts.",
                new AcceptableValueRange<float>(0f, 120f),
                565));
        _supportFillFeatherWidth = plugin.config(
            ConfigSections.ZoneSavior,
            "Zone Bundle Support Fill Feather Width",
            6f,
            ConfigDescriptions.Ordered(
                "Meters around SupportFill footprints that blend back to native terrain. Set to 0 to only change exact footprint cells.",
                new AcceptableValueRange<float>(0f, 64f),
                550));
        _supportFillContactTolerance = plugin.config(
            ConfigSections.ZoneSavior,
            "Support Fill Contact Tolerance",
            0.5f,
            ConfigDescriptions.Ordered(
                "How close loaded source-zone terrain must be to a WearNTear bottom at a 1m x/z cell to be saved as a terrain contact. If the source zone terrain is not loaded, terrain contacts cannot be captured and SupportFill falls back to prefab footprint bounds.",
                new AcceptableValueRange<float>(0.01f, 2f),
                560));
    }
}

internal static class AutoArchiveConfig
{
    private static ConfigEntry<int> _inactiveDays = null!;
    private static ConfigEntry<ZoneSaviorPlugin.Toggle> _dryRun = null!;
    private static ConfigEntry<ZoneSaviorPlugin.Toggle> _resetAfterSave = null!;
    private static ConfigEntry<int> _minimumPiecesPerCluster = null!;
    private static ConfigEntry<int> _maxZonesPerRun = null!;
    private static ConfigEntry<int> _scanIntervalMinutes = null!;
    private static ConfigEntry<int> _scannerBatchSize = null!;

    public static bool Enabled => ScanIntervalMinutes > 0;
    public static int InactiveDays => Mathf.Clamp(_inactiveDays.Value, 0, 3650);
    public static bool DryRun => _dryRun.Value == ZoneSaviorPlugin.Toggle.On;
    public static bool ResetAfterSave => _resetAfterSave.Value == ZoneSaviorPlugin.Toggle.On;
    public static int MinimumPiecesPerCluster => Mathf.Clamp(_minimumPiecesPerCluster.Value, 1, 10000);
    public static int MaxZonesPerRun => Mathf.Clamp(_maxZonesPerRun.Value, 1, 10000);
    public static int ScanIntervalMinutes => Mathf.Clamp(_scanIntervalMinutes.Value, 0, 525600);
    public static int ScannerBatchSize => Mathf.Clamp(_scannerBatchSize.Value, 100, 10000);

    public static void Bind(ZoneSaviorPlugin plugin)
    {
        _dryRun = plugin.config(
            ConfigSections.AutoArchive,
            "Dry Run",
            ZoneSaviorPlugin.Toggle.On,
            ConfigDescriptions.Ordered("If on, auto archive only reports candidate zones and never saves or resets them.", 700));
        _resetAfterSave = plugin.config(
            ConfigSections.AutoArchive,
            "Reset After Save",
            ZoneSaviorPlugin.Toggle.Off,
            ConfigDescriptions.Ordered("If on, saved candidate zones are reset after their bundle is written.", 690));
        _minimumPiecesPerCluster = plugin.config(
            ConfigSections.AutoArchive,
            "Minimum Pieces Per Cluster",
            5,
            ConfigDescriptions.Ordered(
                "Candidate clusters with fewer player structures are not saved. During reset runs, they are reset without saving; otherwise they are skipped.",
                new AcceptableValueRange<int>(1, 10000),
                680));
        _inactiveDays = plugin.config(
            ConfigSections.AutoArchive,
            "Inactive Days",
            30,
            ConfigDescriptions.Ordered(
                "A creator must be unseen for this many days before their zones can be archived. Existing-world owners first discovered by the scanner use that discovery time as their last seen time.",
                new AcceptableValueRange<int>(0, 3650),
                670));
        _scanIntervalMinutes = plugin.config(
            ConfigSections.AutoArchive,
            "Scan Interval Minutes",
            0,
            ConfigDescriptions.Ordered(
                "How often the server runs automatic inactive-structure archive scans. Set to 0 to disable automatic scans. Set to 1 for rapid testing.",
                new AcceptableValueRange<int>(0, 525600),
                660));
        _scannerBatchSize = plugin.config(
            ConfigSections.AutoArchive,
            "Scanner Batch Size",
            1000,
            ConfigDescriptions.Ordered(
                "How many ZDOs the auto archive scanner inspects before yielding a frame.",
                new AcceptableValueRange<int>(100, 10000),
                650));
        _maxZonesPerRun = plugin.config(
            ConfigSections.AutoArchive,
            "Max Zones Per Run",
            50,
            ConfigDescriptions.Ordered(
                "Maximum number of zones to save or reset in one automatic run.",
                new AcceptableValueRange<int>(1, 10000),
                640));
    }
}

using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;

namespace ZoneSavior;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public partial class ZoneSaviorPlugin : BaseUnityPlugin
{
    internal const string ModName = "ZoneSavior";
    internal const string ModVersion = "1.0.4";
    internal const string Author = "sighsorry";
    internal const string ModGUID = $"{Author}.{ModName}";
    internal const string DataStorageFolder = "ZoneSavior";
    internal const string ZoneBundleStorageFolder = "ZoneBundles";
    internal const string ActivityFileName = "activity.yml";

    private const string ConfigFileName = $"{ModGUID}.cfg";
    private const string ZoneRuleFileName = "zones.yml";
    private const long ReloadDelay = TimeSpan.TicksPerSecond;

    private static readonly string ConfigFileFullPath = Path.Combine(Paths.ConfigPath, ConfigFileName);
    internal static readonly string DataStorageFullPath = Path.Combine(Paths.ConfigPath, DataStorageFolder);
    internal static readonly string ZoneBundleStorageFullPath = Path.Combine(DataStorageFullPath, ZoneBundleStorageFolder);
    internal static readonly string ZoneRuleFileFullPath = Path.Combine(DataStorageFullPath, ZoneRuleFileName);

    internal static readonly ManualLogSource ZoneSaviorLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    internal static readonly ConfigSync ConfigSync = new(ModGUID)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion
    };

    internal static string ConnectionError = "";
    internal static ZoneSaviorPlugin Instance { get; private set; } = null!;

    private readonly Harmony _harmony = new(ModGUID);
    private readonly object _reloadLock = new();

    private FileSystemWatcher? _configWatcher;
    private FileSystemWatcher? _zoneRuleWatcher;
    private FileSystemWatcher? _activityWatcher;
    private DateTime _lastConfigReloadTime;
    private DateTime _lastZoneRuleReloadTime;
    private DateTime _lastActivityReloadTime;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        Instance = this;

        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        BindConfiguration();
        EnsureDataDirectories();
        ZoneSaviorFeatureBootstrap.Initialize(ZoneSaviorLogger);
        _harmony.PatchAll(typeof(ZoneSaviorPlugin).Assembly);
        ZoneSaviorFeatureBootstrap.InitializeCompat(ZoneSaviorLogger, _harmony);
        SetupWatchers();

        SaveWithRespectToConfigSet();
        if (saveOnSet)
        {
            Config.SaveOnConfigSet = true;
        }
    }

    private void OnDestroy()
    {
        SaveWithRespectToConfigSet();
        ZoneSaviorFeatureBootstrap.Shutdown();

        _configWatcher?.Dispose();
        _zoneRuleWatcher?.Dispose();
        _activityWatcher?.Dispose();
    }

    private void Update()
    {
        ZoneSaviorFeatureBootstrap.Update();
    }

    private void LateUpdate()
    {
        ZoneSaviorFeatureBootstrap.LateUpdate();
    }

    private void BindConfiguration()
    {
        GeneralConfig.Bind(this);
        ClientConfig.Bind(this);
        ZoneBundleConfig.Bind(this);
        AutoArchiveConfig.Bind(this);
        AdminTerrainToolConfig.Bind(this);
    }

    private void SetupWatchers()
    {
        EnsureDataDirectories();

        _configWatcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _configWatcher.Changed += ReadConfigValues;
        _configWatcher.Created += ReadConfigValues;
        _configWatcher.Renamed += ReadConfigValues;
        _configWatcher.IncludeSubdirectories = false;
        _configWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _configWatcher.EnableRaisingEvents = true;

        _zoneRuleWatcher = new FileSystemWatcher(DataStorageFullPath, ZoneRuleFileName);
        _zoneRuleWatcher.Changed += ReadZoneRuleValues;
        _zoneRuleWatcher.Created += ReadZoneRuleValues;
        _zoneRuleWatcher.Renamed += ReadZoneRuleValues;
        _zoneRuleWatcher.IncludeSubdirectories = false;
        _zoneRuleWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _zoneRuleWatcher.EnableRaisingEvents = true;

        _activityWatcher = new FileSystemWatcher(DataStorageFullPath, ActivityFileName);
        _activityWatcher.Changed += ReadActivityValues;
        _activityWatcher.Created += ReadActivityValues;
        _activityWatcher.Renamed += ReadActivityValues;
        _activityWatcher.IncludeSubdirectories = false;
        _activityWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _activityWatcher.EnableRaisingEvents = true;
    }

    private static void EnsureDataDirectories()
    {
        Directory.CreateDirectory(DataStorageFullPath);
        Directory.CreateDirectory(ZoneBundleStorageFullPath);
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        if (!CanReload(ref _lastConfigReloadTime))
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                ZoneSaviorLogger.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                ZoneSaviorLogger.LogDebug("Reloading configuration...");
                ReloadConfigFromDisk();
                ZoneSaviorLogger.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                ZoneSaviorLogger.LogError($"Error reloading configuration: {ex}");
            }
        }
    }

    private void ReadZoneRuleValues(object sender, FileSystemEventArgs e)
    {
        if (!CanReload(ref _lastZoneRuleReloadTime))
        {
            return;
        }

        lock (_reloadLock)
        {
            try
            {
                ZoneSaviorFeatureBootstrap.ReloadZoneRulesFromDisk();
            }
            catch (Exception ex)
            {
                ZoneSaviorLogger.LogError($"Error reloading zone rule YAML: {ex}");
            }
        }
    }

    private void ReadActivityValues(object sender, FileSystemEventArgs e)
    {
        if (!CanReload(ref _lastActivityReloadTime))
        {
            return;
        }

        lock (_reloadLock)
        {
            try
            {
                bool reloaded = ZoneSaviorFeatureBootstrap.TryReloadActivityFromDisk(out string message);
                if (reloaded)
                {
                    ZoneSaviorLogger.LogInfo(message);
                }
                else
                {
                    ZoneSaviorLogger.LogDebug(message);
                }
            }
            catch (Exception ex)
            {
                ZoneSaviorLogger.LogError($"Error reloading activity YAML: {ex}");
            }
        }
    }

    private static bool CanReload(ref DateTime lastReloadTime)
    {
        DateTime now = DateTime.Now;
        if (now.Ticks - lastReloadTime.Ticks < ReloadDelay)
        {
            return false;
        }

        lastReloadTime = now;
        return true;
    }

    private void SaveWithRespectToConfigSet(bool reload = false)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        if (reload)
        {
            Config.Reload();
        }

        Config.Save();
        Config.SaveOnConfigSet = originalSaveOnSet;
    }

    private void ReloadConfigFromDisk()
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        Config.Reload();
        Config.SaveOnConfigSet = originalSaveOnSet;
    }

    internal ConfigEntry<T> config<T>(
        string group,
        string name,
        T value,
        ConfigDescription description,
        bool synchronizedSetting = true)
    {
        ConfigDescription extendedDescription = new(
            description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Client Only]"),
            description.AcceptableValues,
            description.Tags);

        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;
        return configEntry;
    }

    internal ConfigEntry<T> config<T>(
        string group,
        string name,
        T value,
        string description,
        bool synchronizedSetting = true)
    {
        return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
    }
}

public static class ToggleExtensions
{
    extension(ZoneSaviorPlugin.Toggle value)
    {
        public bool IsOn()
        {
            return value == ZoneSaviorPlugin.Toggle.On;
        }

    }
}

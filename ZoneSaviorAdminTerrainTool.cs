using System;
using System.Collections;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    public const string PrefabName = "ZoneSaviorTerrainProxy";
    public const string SlopePrefabName = "ZoneSaviorTerrainProxySlope";
    public const string PaintPrefabName = "ZoneSaviorPaintProxy";
    public const string ResetPrefabName = "ZoneSaviorTerrainReset";

    private const int DataVersion = 11;
    private const float AdminTerrainMaxDelta = 1024f;
    private const float MinCircleRadius = 0.5f;
    private const float MaxCircleRadius = 128f;
    private const float MinSlopeSize = 0.5f;
    private const float MaxSlopeSize = 256f;
    private const float TerrainEdgeSoftnessStep = 0.05f;
    private const float SearchPadding = 4f;
    private const float AreaLineWidth = 0.05f;
    private const float PreviewPositionEpsilon = 0.01f;
    private const float PreviewFloatEpsilon = 0.01f;
    private const string AreaLineName = "ZoneSaviorTerrainProxyArea";

    private static readonly int PrefabHash = StringExtensionMethods.GetStableHashCode(PrefabName);
    private static readonly int SlopePrefabHash = StringExtensionMethods.GetStableHashCode(SlopePrefabName);
    private static readonly int PaintPrefabHash = StringExtensionMethods.GetStableHashCode(PaintPrefabName);
    private static readonly int ResetPrefabHash = StringExtensionMethods.GetStableHashCode(ResetPrefabName);
    private static readonly int VersionHash = Hash("version");
    private static readonly int ModeHash = Hash("mode");
    private static readonly int AppliedHash = Hash("applied");
    private static readonly int AppliedPositionHash = Hash("applied_position");
    private static readonly int AppliedYawHash = Hash("applied_yaw");
    private static readonly int AppliedSettingsHash = Hash("applied_settings");
    private static readonly int RadiusHash = Hash("radius");
    private static readonly int WidthHash = Hash("width");
    private static readonly int LengthHash = Hash("length");
    private static readonly int SlopeHeightDeltaHash = Hash("slope_height_delta");
    private static readonly int TerrainEdgeSoftnessHash = Hash("terrain_edge_softness");
    private static readonly int PaintTypeHash = Hash("paint_type");

    private static ManualLogSource _logger = null!;
    private static GameObject? _prefab;
    private static GameObject? _slopePrefab;
    private static GameObject? _paintPrefab;
    private static GameObject? _resetPrefab;
    private static Sprite? _icon;
    private static Sprite? _slopeIcon;
    private static Sprite? _paintIcon;
    private static Sprite? _resetIcon;
    private static GameObject? _previewRoot;
    private static LineRenderer? _areaLine;
    private static Material? _lineMaterial;
    private static ZNetScene? _registeredScene;
    private static float _runtimeRadius = float.NaN;
    private static float _lastConfigRadius = float.NaN;
    private static float _runtimeSlopeWidth = float.NaN;
    private static float _lastConfigSlopeWidth = float.NaN;
    private static float _runtimeTerrainEdgeSoftness = float.NaN;
    private static float _lastConfigTerrainEdgeSoftness = float.NaN;
    private static AdminTerrainPaintType _runtimePaintType;
    private static AdminTerrainPaintType _lastConfigPaintType;
    private static bool _runtimePaintTypeInitialized;
    private static TerrainResetScope _terrainResetScope;
    private static bool _hasPendingSlopeStart;
    private static Vector3 _pendingSlopeStart;
    private static int _pendingSlopeStartFrame;
    private static PreviewKind _lastPreviewKind = PreviewKind.None;
    private static Vector3 _lastPreviewPosition;
    private static float _lastPreviewYaw;
    private static float _lastPreviewRadius;
    private static float _lastPreviewWidth;
    private static float _lastPreviewLength;
    private static float _lastPreviewHeightDelta;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        ResetTerrainResetScope();
    }

    public static void Update()
    {
        EnsureRegistered();
        AdminTerrainInfinityHammerCompat.Update();
        UpdatePlacementSizingInput();
        if (!Player.m_localPlayer)
        {
            HideAreaLine();
        }
    }

    public static void InitializeCompat(Harmony harmony)
    {
        AdminTerrainInfinityHammerCompat.Initialize(_logger, harmony);
    }

    public static void Shutdown()
    {
        if (_prefab)
        {
            UnityEngine.Object.Destroy(_prefab);
        }

        if (_slopePrefab)
        {
            UnityEngine.Object.Destroy(_slopePrefab);
        }

        if (_paintPrefab)
        {
            UnityEngine.Object.Destroy(_paintPrefab);
        }

        if (_resetPrefab)
        {
            UnityEngine.Object.Destroy(_resetPrefab);
        }

        ReleasePaintGridPreviewResources();
        if (_previewRoot)
        {
            UnityEngine.Object.Destroy(_previewRoot);
        }

        if (_lineMaterial)
        {
            UnityEngine.Object.Destroy(_lineMaterial);
        }

        _prefab = null;
        _slopePrefab = null;
        _paintPrefab = null;
        _resetPrefab = null;
        _previewRoot = null;
        _areaLine = null;
        _lineMaterial = null;
        _registeredScene = null;
        AdminTerrainInfinityHammerCompat.Shutdown();
        _hasPendingSlopeStart = false;
        _pendingSlopeStartFrame = 0;
        _lastPreviewKind = PreviewKind.None;
        ResetTerrainResetScope();
    }

    public static bool IsProxyPiece(Piece piece)
    {
        return piece && IsProxyPrefabName(Utils.GetPrefabName(piece.gameObject));
    }

    public static bool IsProxyObject(GameObject obj)
    {
        return obj && IsProxyPrefabName(Utils.GetPrefabName(obj));
    }

    private static bool IsSlopeProxyObject(GameObject obj)
    {
        return obj && string.Equals(Utils.GetPrefabName(obj), SlopePrefabName, StringComparison.Ordinal);
    }

    private static bool IsPaintProxyObject(GameObject obj)
    {
        return obj && string.Equals(Utils.GetPrefabName(obj), PaintPrefabName, StringComparison.Ordinal);
    }

    private static bool IsResetProxyObject(GameObject obj)
    {
        return obj && string.Equals(Utils.GetPrefabName(obj), ResetPrefabName, StringComparison.Ordinal);
    }

    private static bool IsProxyPrefabName(string prefabName)
    {
        return string.Equals(prefabName, PrefabName, StringComparison.Ordinal) ||
               string.Equals(prefabName, SlopePrefabName, StringComparison.Ordinal) ||
               string.Equals(prefabName, PaintPrefabName, StringComparison.Ordinal) ||
               string.Equals(prefabName, ResetPrefabName, StringComparison.Ordinal);
    }

    public static void ConfigurePlacedProxy(ZoneSaviorTerrainProxy proxy)
    {
        EnsurePlacedProxyInitialized(proxy);

        ZNetView? nview = proxy.GetComponent<ZNetView>();
        ZDO zdo = nview ? nview.GetZDO() : null!;
        if (zdo == null)
        {
            ShowMessage("ZoneSavior terrain proxy could not initialize ZDO data.");
            return;
        }

        if (!IsAdmin())
        {
            ShowMessage("ZoneSavior terrain proxy is admin only.");
            proxy.QueueDestroy();
            return;
        }

        bool newMarker = !HasStoredSettings(zdo);
        if (newMarker && IsResetProxyObject(proxy.gameObject))
        {
            HandleResetPlacement(proxy, nview);
            return;
        }

        if (newMarker)
        {
            if (IsSlopeProxyObject(proxy.gameObject))
            {
                if (!ConfigureNewSlopeProxy(proxy, zdo))
                {
                    return;
                }
            }
            else if (IsPaintProxyObject(proxy.gameObject))
            {
                zdo.SetPosition(proxy.transform.position);
                zdo.SetRotation(proxy.transform.rotation);
                WriteSettings(zdo, CurrentPaintSettings());
            }
            else
            {
                zdo.SetPosition(proxy.transform.position);
                zdo.SetRotation(proxy.transform.rotation);
                WriteSettings(zdo, CurrentSettings());
            }
        }

        zdo.Set(AppliedHash, false);
        if (!ApplyStoredSettings(proxy, force: true, notify: true))
        {
            proxy.QueueApplyRetry();
        }
    }

    private static void HandleResetPlacement(ZoneSaviorTerrainProxy proxy, ZNetView? placedView)
    {
        Vector3 position = proxy.transform.position;
        Quaternion rotation = proxy.transform.rotation;
        TerrainProxySettings settings = CurrentSettings();
        TerrainResetScope resetScope = CurrentTerrainResetScope;

        if (!TryResetTerrainAndIntersectingProxyObjects(
            position,
            rotation,
            settings,
            resetScope,
            placedView,
            out int terrainCompilers,
            out int removed,
            out string failureReason))
        {
            ShowMessage(
                $"ZoneSavior {GetTerrainResetScopeLabel(resetScope)} reset was not applied: " +
                $"{failureReason}. No terrain proxy objects were removed.");
            proxy.QueueDestroy();
            return;
        }

        ShowMessage(
            $"ZoneSavior {GetTerrainResetScopeLabel(resetScope)} reset {terrainCompilers} terrain compiler(s), " +
            $"removed {removed} terrain proxy object(s).");
        proxy.QueueDestroy();
    }

    public static void ApplyLoadedProxy(ZoneSaviorTerrainProxy proxy)
    {
        if (!proxy || ZNetView.m_ghostInit || ZNetView.m_useInitZDO || ZNetView.m_initZDO != null)
        {
            return;
        }

        if (!TryApplyLoadedProxy(proxy))
        {
            proxy.QueueApplyRetry();
        }
    }

    internal static bool TryApplyLoadedProxy(ZoneSaviorTerrainProxy proxy)
    {
        if (!proxy || ZNetView.m_ghostInit || ZNetView.m_useInitZDO || ZNetView.m_initZDO != null)
        {
            return false;
        }

        ZNetView? nview = proxy.GetComponent<ZNetView>();
        ZDO zdo = nview ? nview.GetZDO() : null!;
        if (zdo == null || !HasStoredSettings(zdo))
        {
            return true;
        }

        TerrainProxySettings settings = ReadSettings(zdo);
        bool alreadyApplied = zdo.GetBool(AppliedHash, false);
        if (alreadyApplied &&
            (TrySeedMissingAppliedMarkers(zdo, proxy.transform, settings) ||
             HasAppliedAtTransform(zdo, proxy.transform, settings)))
        {
            return true;
        }

        return ApplyStoredSettings(proxy, force: alreadyApplied);
    }

    public static bool TryCreateLoadedProxy(ZDO zdo, ref GameObject result)
    {
        if (zdo == null)
        {
            return false;
        }

        GameObject? prefab = GetRegisteredPrefab(zdo.GetPrefab());
        if (!prefab)
        {
            return false;
        }

        bool previousUseInitZdo = ZNetView.m_useInitZDO;
        ZDO? previousInitZdo = ZNetView.m_initZDO;
        bool created = false;
        try
        {
            try
            {
                ZNetView.m_useInitZDO = true;
                ZNetView.m_initZDO = zdo;
                result = UnityEngine.Object.Instantiate(prefab, zdo.GetPosition(), zdo.GetRotation());
                if (result && !result.activeSelf)
                {
                    result.SetActive(true);
                }

                if (ZNetView.m_initZDO != null)
                {
                    _logger?.LogWarning($"ZoneSavior terrain object failed to consume ZDO {zdo.m_uid}.");
                }

                created = result != null;
            }
            finally
            {
                ZNetView.m_initZDO = null;
                ZNetView.m_useInitZDO = false;
            }

            if (created && result)
            {
                ZoneSaviorTerrainProxy proxy = result.GetComponent<ZoneSaviorTerrainProxy>();
                if (proxy)
                {
                    ApplyLoadedProxy(proxy);
                }
            }

            return created;
        }
        finally
        {
            ZNetView.m_initZDO = previousInitZdo;
            ZNetView.m_useInitZDO = previousUseInitZdo;
        }
    }

    public static void PrepareInfinityHammerPlaced(GameObject obj)
    {
        if (IsProxyObject(obj))
        {
            ZoneSaviorTerrainProxy proxy = obj.GetComponent<ZoneSaviorTerrainProxy>();
            if (proxy)
            {
                EnsurePlacedProxyInitialized(proxy);
            }
        }
    }

    public static bool ShouldRunInfinityHammerNoCreator(ZNetView view, Piece piece)
    {
        if (!view || !piece)
        {
            return false;
        }

        if (view.GetZDO() != null)
        {
            return true;
        }

        PrepareInfinityHammerPlaced(piece.gameObject);
        return view.GetZDO() != null;
    }

    internal static void ApplyInfinityHammerPlaced(GameObject obj, bool directMenuSelection)
    {
        if (!IsProxyObject(obj))
        {
            return;
        }

        ZoneSaviorTerrainProxy proxy = obj.GetComponent<ZoneSaviorTerrainProxy>();
        if (!proxy)
        {
            return;
        }

        EnsurePlacedProxyInitialized(proxy);
        ZNetView nview = proxy.GetComponent<ZNetView>();
        ZDO zdo = nview ? nview.GetZDO() : null!;
        if (directMenuSelection && zdo != null && !HasStoredSettings(zdo))
        {
            ConfigurePlacedProxy(proxy);
            return;
        }

        ApplyLoadedProxy(proxy);
    }

    private static TerrainProxySettings CurrentSettings()
    {
        return new TerrainProxySettings(
            TerrainProxyMode.Circle,
            CurrentCircleRadius(),
            AdminTerrainToolConfig.SlopeWidth,
            AdminTerrainToolConfig.SlopeWidth,
            0f,
            CurrentTerrainEdgeSoftness(),
            AdminTerrainPaintType.Grass);
    }

    private static TerrainProxySettings CurrentPaintSettings()
    {
        return new TerrainProxySettings(
            TerrainProxyMode.Paint,
            CurrentCircleRadius(),
            AdminTerrainToolConfig.SlopeWidth,
            AdminTerrainToolConfig.SlopeWidth,
            0f,
            0f,
            CurrentPaintType());
    }

    private static void EnsurePlacedProxyInitialized(ZoneSaviorTerrainProxy proxy)
    {
        EnsurePlacedObjectInitialized(proxy.gameObject);
    }

    private static void EnsurePlacedObjectInitialized(GameObject obj)
    {
        if (!obj.activeSelf)
        {
            obj.SetActive(true);
        }

        Piece piece = obj.GetComponent<Piece>();
        ZNetView nview = obj.GetComponent<ZNetView>();
        if (piece != null && nview != null && piece.m_nview == null)
        {
            piece.m_nview = nview;
        }
    }

    private static bool ShouldShowTo(Player player)
    {
        return player != null &&
               player == Player.m_localPlayer &&
               Player.m_debugMode &&
               IsAdmin();
    }

    private static bool IsAdmin()
    {
        if (ZNet.instance == null)
        {
            return false;
        }

        return ZNet.instance.LocalPlayerIsAdminOrHost() || IsCheatsEnabled();
    }

    private static bool IsCheatsEnabled()
    {
        return global::Console.instance != null &&
               ((Terminal)global::Console.instance).IsCheatsEnabled();
    }

    private static bool HasStoredSettings(ZDO zdo)
    {
        return zdo.GetInt(VersionHash, 0) == DataVersion;
    }

    private static TerrainProxySettings SlopeSettings(SlopePlacement placement)
    {
        return new TerrainProxySettings(
            TerrainProxyMode.Slope,
            CurrentCircleRadius(),
            placement.Width,
            placement.Length,
            placement.HeightDelta,
            0f,
            AdminTerrainPaintType.Grass);
    }

    private static void WriteSettings(ZDO zdo, TerrainProxySettings settings)
    {
        zdo.Set(VersionHash, DataVersion);
        zdo.Set(ModeHash, (int)settings.Mode);
        zdo.Set(RadiusHash, settings.Radius);
        zdo.Set(WidthHash, settings.Width);
        zdo.Set(LengthHash, settings.Length);
        zdo.Set(SlopeHeightDeltaHash, settings.SlopeHeightDelta);
        zdo.Set(TerrainEdgeSoftnessHash, settings.TerrainEdgeSoftness);
        zdo.Set(PaintTypeHash, (int)settings.PaintType);
    }

    private static TerrainProxySettings ReadSettings(ZDO zdo)
    {
        TerrainProxyMode defaultMode = zdo.GetPrefab() switch
        {
            int prefabHash when prefabHash == SlopePrefabHash => TerrainProxyMode.Slope,
            int prefabHash when prefabHash == PaintPrefabHash => TerrainProxyMode.Paint,
            _ => TerrainProxyMode.Circle
        };
        TerrainProxyMode mode = (TerrainProxyMode)zdo.GetInt(
            ModeHash,
            (int)defaultMode);

        return new TerrainProxySettings(
            mode,
            Mathf.Clamp(zdo.GetFloat(RadiusHash, 8f), MinCircleRadius, MaxCircleRadius),
            Mathf.Clamp(zdo.GetFloat(WidthHash, 8f), MinSlopeSize, MaxSlopeSize),
            Mathf.Clamp(zdo.GetFloat(LengthHash, 8f), MinSlopeSize, MaxSlopeSize),
            Mathf.Clamp(zdo.GetFloat(SlopeHeightDeltaHash, 0f), -AdminTerrainMaxDelta, AdminTerrainMaxDelta),
            Mathf.Clamp01(zdo.GetFloat(TerrainEdgeSoftnessHash, 0f)),
            (AdminTerrainPaintType)zdo.GetInt(PaintTypeHash, (int)AdminTerrainPaintType.Dirt));
    }

    internal static void DestroyProxy(ZNetView nview)
    {
        if (!nview || !nview.IsValid())
        {
            return;
        }

        if (!nview.IsOwner())
        {
            nview.ClaimOwnership();
        }

        ZNetScene scene = ZNetScene.instance;
        if (scene != null)
        {
            scene.Destroy(nview.gameObject);
            return;
        }

        nview.Destroy();
    }

    public static float GetTerrainCompHeightClampLimit()
    {
        return AdminTerrainMaxDelta;
    }

    private static bool HasAppliedAtTransform(
        ZDO zdo,
        Transform transform,
        TerrainProxySettings settings)
    {
        if (!HasAppliedAtPosition(zdo, transform.position) ||
            zdo.GetInt(AppliedSettingsHash, int.MinValue) != GetSettingsFingerprint(settings))
        {
            return false;
        }

        if (settings.Mode != TerrainProxyMode.Slope)
        {
            return true;
        }

        float appliedYaw = zdo.GetFloat(AppliedYawHash, float.NaN);
        return !float.IsNaN(appliedYaw) &&
               Mathf.Abs(Mathf.DeltaAngle(appliedYaw, transform.rotation.eulerAngles.y)) < 0.1f;
    }

    private static bool TrySeedMissingAppliedMarkers(
        ZDO zdo,
        Transform transform,
        TerrainProxySettings settings)
    {
        if (zdo.GetInt(AppliedSettingsHash, out _) ||
            !HasAppliedAtPosition(zdo, transform.position))
        {
            return false;
        }

        zdo.Set(AppliedSettingsHash, GetSettingsFingerprint(settings));
        if (settings.Mode == TerrainProxyMode.Slope)
        {
            zdo.Set(AppliedYawHash, transform.rotation.eulerAngles.y);
        }

        return true;
    }

    private static bool HasAppliedAtPosition(ZDO zdo, Vector3 position)
    {
        return zdo.GetVec3(AppliedPositionHash, out Vector3 appliedPosition) &&
               Utils.DistanceXZ(appliedPosition, position) < 0.1f &&
               Mathf.Abs(appliedPosition.y - position.y) < 0.1f;
    }

    private static int GetSettingsFingerprint(TerrainProxySettings settings)
    {
        unchecked
        {
            int hash = (int)settings.Mode;
            hash = hash * 31 + Mathf.RoundToInt(settings.Radius * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(settings.Width * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(settings.Length * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(settings.SlopeHeightDelta * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(settings.TerrainEdgeSoftness * 1000f);
            hash = hash * 31 + (int)settings.PaintType;
            return hash;
        }
    }

    private static void ReportTerrainResult(string message, bool notify)
    {
        if (notify)
        {
            ShowMessage(message);
        }
        else
        {
            _logger?.LogDebug(message);
        }
    }

    private static void ShowMessage(string message)
    {
        _logger?.LogInfo(message);
        Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, message);
    }

    private static int Hash(string key)
    {
        return StringExtensionMethods.GetStableHashCode($"{ZoneSaviorPlugin.ModGUID}.terrain_proxy.{key}");
    }

    private static TerrainResetScope CurrentTerrainResetScope => _terrainResetScope;

    private static void ToggleTerrainResetScope()
    {
        _terrainResetScope = _terrainResetScope == TerrainResetScope.TerrainAndPaint
            ? TerrainResetScope.PaintOnly
            : TerrainResetScope.TerrainAndPaint;
    }

    private static void ResetTerrainResetScope()
    {
        _terrainResetScope = TerrainResetScope.TerrainAndPaint;
    }

    private static string GetTerrainResetScopeLabel(TerrainResetScope resetScope)
    {
        return resetScope == TerrainResetScope.PaintOnly ? "paint-only" : "terrain + paint";
    }

    private enum TerrainResetScope
    {
        TerrainAndPaint,
        PaintOnly
    }

    private enum TerrainProxyMode
    {
        Circle = 0,
        Slope = 1,
        Paint = 2
    }

    private readonly struct SlopePlacement
    {
        public SlopePlacement(Vector3 center, Quaternion rotation, float width, float length, float heightDelta)
        {
            Center = center;
            Rotation = rotation;
            Width = width;
            Length = length;
            HeightDelta = heightDelta;
        }

        public Vector3 Center { get; }
        public Quaternion Rotation { get; }
        public float Width { get; }
        public float Length { get; }
        public float HeightDelta { get; }

        public Vector3 GetWorldPoint(float localX, float localZ)
        {
            Vector3 point = Center + Rotation * new Vector3(localX, 0f, localZ);
            point.y = Center.y + Mathf.Clamp(localZ / Mathf.Max(Length, 0.001f), -0.5f, 0.5f) * HeightDelta;
            return point;
        }
    }

    private readonly struct TerrainProxySettings
    {
        public TerrainProxySettings(
            TerrainProxyMode mode,
            float radius,
            float width,
            float length,
            float slopeHeightDelta,
            float terrainEdgeSoftness,
            AdminTerrainPaintType paintType)
        {
            Mode = mode;
            Radius = Mathf.Clamp(radius, MinCircleRadius, MaxCircleRadius);
            Width = Mathf.Clamp(width, MinSlopeSize, MaxSlopeSize);
            Length = Mathf.Clamp(length, MinSlopeSize, MaxSlopeSize);
            SlopeHeightDelta = Mathf.Clamp(slopeHeightDelta, -AdminTerrainMaxDelta, AdminTerrainMaxDelta);
            TerrainEdgeSoftness = Mathf.Clamp01(terrainEdgeSoftness);
            PaintType = paintType;
        }

        public TerrainProxyMode Mode { get; }
        public float Radius { get; }
        public float Width { get; }
        public float Length { get; }
        public float SlopeHeightDelta { get; }
        public float TerrainEdgeSoftness { get; }
        public AdminTerrainPaintType PaintType { get; }
        public bool ModifiesHeight => Mode is TerrainProxyMode.Circle or TerrainProxyMode.Slope;
        public bool ModifiesPaint => Mode == TerrainProxyMode.Paint;
        public bool HasCircleFootprint => Mode is TerrainProxyMode.Circle or TerrainProxyMode.Paint;
        public bool HasIdempotentHeightApplication =>
            ModifiesHeight && (Mode != TerrainProxyMode.Circle || TerrainEdgeSoftness <= 0f);

        public float TerrainRadius
        {
            get
            {
                return Mode == TerrainProxyMode.Slope
                    ? Mathf.Sqrt(Width * Width + Length * Length) * 0.5f
                    : Radius;
            }
        }

        public float SearchRadius => TerrainRadius;

        public float GetNormalizedDistance(float x, float z)
        {
            if (Mode == TerrainProxyMode.Slope)
            {
                return Mathf.Max(Mathf.Abs(x) / (Width * 0.5f), Mathf.Abs(z) / (Length * 0.5f));
            }

            return Mathf.Sqrt(x * x + z * z) / Radius;
        }

        public float GetTargetHeight(Vector3 center, float heightmapWorldY, float localZ)
        {
            float worldHeight = center.y;
            if (Mode == TerrainProxyMode.Slope)
            {
                worldHeight += Mathf.Clamp(localZ / Length, -0.5f, 0.5f) * SlopeHeightDelta;
            }

            return worldHeight - heightmapWorldY;
        }

        public float GetLevelFalloff(float x, float z)
        {
            if (Mode != TerrainProxyMode.Circle || TerrainEdgeSoftness <= 0f)
            {
                return 1f;
            }

            float distance = Mathf.Sqrt(x * x + z * z);
            float topRadiusRatio = Mathf.Pow(1f - TerrainEdgeSoftness, 1.5f);
            float topRadius = Radius * topRadiusRatio;
            if (distance <= topRadius)
            {
                return 1f;
            }

            float sideWidth = Mathf.Max(Radius - topRadius, 0.001f);
            float edgeProgress = Mathf.Clamp01((Radius - distance) / sideWidth);
            float sideHardness = Mathf.Lerp(1f, 4f, TerrainEdgeSoftness);
            return Mathf.Pow(edgeProgress, 1f / sideHardness);
        }
    }
}

internal sealed class ZoneSaviorTerrainProxy : MonoBehaviour, IPlaced
{
    private const int ApplyRetryAttempts = 120;
    private const float ApplyRetrySeconds = 0.5f;
    private const float SlowApplyRetrySeconds = 10f;
    private const float SlowRetryLogInterval = 60f;

    private static float _nextSlowRetryLogTime;

    private Coroutine? _destroyRoutine;
    private Coroutine? _applyRoutine;
    private bool _hasAppliedPreparedBatch;
    private ulong _appliedPreparedBatchFingerprint;

    private void Awake()
    {
        AdminTerrainTool.ApplyLoadedProxy(this);
    }

    public void OnPlaced()
    {
        AdminTerrainTool.ConfigurePlacedProxy(this);
    }

    public void QueueApplyRetry()
    {
        if (_applyRoutine != null || !isActiveAndEnabled)
        {
            return;
        }

        try
        {
            _applyRoutine = StartCoroutine(RetryApplyWhenTerrainIsReady());
            if (_applyRoutine == null)
            {
                ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning(
                    "ZoneSavior terrain proxy could not start its terrain-ready retry coroutine.");
            }
        }
        catch (Exception ex)
        {
            _applyRoutine = null;
            ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning(
                $"ZoneSavior terrain proxy could not start its terrain-ready retry coroutine: {ex.Message}");
        }
    }

    public void QueueDestroy()
    {
        if (_destroyRoutine != null)
        {
            return;
        }

        _destroyRoutine = StartCoroutine(DestroyNextFrame());
    }

    internal bool HasAppliedPreparedBatch(ulong fingerprint)
    {
        return _hasAppliedPreparedBatch && _appliedPreparedBatchFingerprint == fingerprint;
    }

    internal void MarkPreparedBatchApplied(ulong fingerprint)
    {
        _appliedPreparedBatchFingerprint = fingerprint;
        _hasAppliedPreparedBatch = true;
    }

    private IEnumerator DestroyNextFrame()
    {
        yield return null;

        ZNetView nview = GetComponent<ZNetView>();
        if (nview)
        {
            AdminTerrainTool.DestroyProxy(nview);
        }
    }

    private IEnumerator RetryApplyWhenTerrainIsReady()
    {
        WaitForSeconds delay = new(ApplyRetrySeconds);
        try
        {
            for (int attempt = 0; attempt < ApplyRetryAttempts; attempt++)
            {
                yield return delay;
                if (AdminTerrainTool.TryApplyLoadedProxy(this))
                {
                    yield break;
                }
            }

            if (Time.realtimeSinceStartup >= _nextSlowRetryLogTime)
            {
                _nextSlowRetryLogTime = Time.realtimeSinceStartup + SlowRetryLogInterval;
                ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning(
                    $"ZoneSavior terrain proxy at {transform.position} is still waiting for complete terrain coverage; " +
                    "continuing at a lower retry frequency.");
            }

            WaitForSeconds slowDelay = new(SlowApplyRetrySeconds);
            while (true)
            {
                yield return slowDelay;
                if (AdminTerrainTool.TryApplyLoadedProxy(this))
                {
                    yield break;
                }
            }
        }
        finally
        {
            _applyRoutine = null;
        }
    }
}

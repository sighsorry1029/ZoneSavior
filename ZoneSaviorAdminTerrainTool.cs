using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
    public const string PaintResetPrefabName = "ZoneSaviorPaintReset";

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
    private static readonly int PaintResetPrefabHash = StringExtensionMethods.GetStableHashCode(PaintResetPrefabName);
    private static readonly int VersionHash = Hash("version");
    private static readonly int ModeHash = Hash("mode");
    private static readonly int AppliedHash = Hash("applied");
    private static readonly int AppliedPositionHash = Hash("applied_position");
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
    private static GameObject? _paintResetPrefab;
    private static Sprite? _icon;
    private static Sprite? _slopeIcon;
    private static Sprite? _paintIcon;
    private static Sprite? _resetIcon;
    private static Sprite? _paintResetIcon;
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
    }

    public static void Update()
    {
        EnsureRegistered();
        AdminTerrainInfinityHammerCompat.Update();
        UpdatePlacementSizingInput();
    }

    public static void LateUpdate()
    {
        UpdatePlacementPreview();
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

        if (_paintResetPrefab)
        {
            UnityEngine.Object.Destroy(_paintResetPrefab);
        }

        if (_previewRoot)
        {
            UnityEngine.Object.Destroy(_previewRoot);
        }

        _prefab = null;
        _slopePrefab = null;
        _paintPrefab = null;
        _resetPrefab = null;
        _paintResetPrefab = null;
        _previewRoot = null;
        _areaLine = null;
        _lineMaterial = null;
        _registeredScene = null;
        AdminTerrainInfinityHammerCompat.Shutdown();
        _hasPendingSlopeStart = false;
        _pendingSlopeStartFrame = 0;
        _lastPreviewKind = PreviewKind.None;
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

    private static bool IsPaintResetProxyObject(GameObject obj)
    {
        return obj && string.Equals(Utils.GetPrefabName(obj), PaintResetPrefabName, StringComparison.Ordinal);
    }

    private static bool IsProxyPrefabName(string prefabName)
    {
        return string.Equals(prefabName, PrefabName, StringComparison.Ordinal) ||
               string.Equals(prefabName, SlopePrefabName, StringComparison.Ordinal) ||
               string.Equals(prefabName, PaintPrefabName, StringComparison.Ordinal) ||
               string.Equals(prefabName, ResetPrefabName, StringComparison.Ordinal) ||
               string.Equals(prefabName, PaintResetPrefabName, StringComparison.Ordinal);
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

        if (newMarker && IsPaintResetProxyObject(proxy.gameObject))
        {
            HandlePaintResetPlacement(proxy, nview);
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
                WriteCurrentSettings(zdo);
            }
        }

        zdo.Set(AppliedHash, false);
        ApplyStoredSettings(proxy, force: true, notify: true);
    }

    private static void HandleResetPlacement(ZoneSaviorTerrainProxy proxy, ZNetView? placedView)
    {
        Vector3 position = proxy.transform.position;
        Quaternion rotation = proxy.transform.rotation;
        TerrainProxySettings settings = CurrentSettings();

        int terrainCompilers = ResetTerrain(position, rotation, settings);
        int removed = ResetIntersectingProxyObjects(position, rotation, settings, placedView, out int proxyTerrainCompilers);
        terrainCompilers += proxyTerrainCompilers;
        ShowMessage($"ZoneSavior terrain reset {terrainCompilers} terrain compiler(s), removed {removed} terrain proxy object(s).");
        proxy.QueueDestroy();
    }

    private static void HandlePaintResetPlacement(ZoneSaviorTerrainProxy proxy, ZNetView? placedView)
    {
        Vector3 position = proxy.transform.position;
        Quaternion rotation = proxy.transform.rotation;
        TerrainProxySettings settings = CurrentPaintSettings();

        int terrainCompilers = ResetTerrain(position, rotation, settings);
        int removed = ResetIntersectingPaintProxyObjects(position, rotation, settings, placedView, out int proxyTerrainCompilers);
        terrainCompilers += proxyTerrainCompilers;
        ShowMessage($"ZoneSavior paint reset {terrainCompilers} terrain compiler(s), removed {removed} paint proxy object(s).");
        proxy.QueueDestroy();
    }

    public static void ApplyLoadedProxy(ZoneSaviorTerrainProxy proxy)
    {
        ZNetView? nview = proxy.GetComponent<ZNetView>();
        ZDO zdo = nview ? nview.GetZDO() : null!;
        if (zdo == null || !HasStoredSettings(zdo))
        {
            return;
        }

        bool alreadyApplied = zdo.GetBool(AppliedHash, false);
        if (alreadyApplied && HasAppliedAtPosition(zdo, proxy.transform.position))
        {
            return;
        }

        ApplyStoredSettings(proxy, force: alreadyApplied);
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

        ZNetView.m_useInitZDO = true;
        ZNetView.m_initZDO = zdo;
        try
        {
            result = UnityEngine.Object.Instantiate(prefab, zdo.GetPosition(), zdo.GetRotation());
            if (result && !result.activeSelf)
            {
                result.SetActive(true);
            }

            if (ZNetView.m_initZDO != null)
            {
                _logger?.LogWarning($"ZoneSavior terrain object failed to consume ZDO {zdo.m_uid}.");
            }

            return result != null;
        }
        finally
        {
            ZNetView.m_initZDO = null;
            ZNetView.m_useInitZDO = false;
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

    internal static void ApplyInfinityHammerPlaced(GameObject obj)
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

    private static int ResetIntersectingProxyObjects(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        ZNetView? placedView,
        out int terrainCompilers)
    {
        terrainCompilers = 0;
        ZNetScene scene = ZNetScene.instance;
        if (scene == null)
        {
            return 0;
        }

        List<ZNetView> views = scene.m_instances.Values.ToList();
        int removed = 0;
        foreach (ZNetView view in views)
        {
            if (!view || view == placedView || !IsProxyObject(view.gameObject))
            {
                continue;
            }

            ZDO zdo = view.GetZDO();
            if (zdo == null || !HasStoredSettings(zdo))
            {
                continue;
            }

            TerrainProxySettings proxySettings = ReadSettings(zdo);
            Vector3 proxyPosition = view.transform.position;
            Quaternion proxyRotation = view.transform.rotation;
            if (!FootprintsIntersect(position, rotation, settings, proxyPosition, proxyRotation, proxySettings))
            {
                continue;
            }

            terrainCompilers += ResetTerrain(proxyPosition, proxyRotation, proxySettings);
            DestroyProxy(view);
            removed++;
        }

        return removed;
    }

    private static int ResetIntersectingPaintProxyObjects(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        ZNetView? placedView,
        out int terrainCompilers)
    {
        terrainCompilers = 0;
        ZNetScene scene = ZNetScene.instance;
        if (scene == null)
        {
            return 0;
        }

        List<ZNetView> views = scene.m_instances.Values.ToList();
        int removed = 0;
        foreach (ZNetView view in views)
        {
            if (!view || view == placedView || !IsPaintProxyObject(view.gameObject))
            {
                continue;
            }

            ZDO zdo = view.GetZDO();
            if (zdo == null || !HasStoredSettings(zdo))
            {
                continue;
            }

            TerrainProxySettings proxySettings = ReadSettings(zdo);
            Vector3 proxyPosition = view.transform.position;
            Quaternion proxyRotation = view.transform.rotation;
            if (!FootprintsIntersect(position, rotation, settings, proxyPosition, proxyRotation, proxySettings))
            {
                continue;
            }

            terrainCompilers += ResetTerrain(proxyPosition, proxyRotation, proxySettings);
            DestroyProxy(view);
            removed++;
        }

        return removed;
    }

    private static bool FootprintsIntersect(
        Vector3 aPosition,
        Quaternion aRotation,
        TerrainProxySettings aSettings,
        Vector3 bPosition,
        Quaternion bRotation,
        TerrainProxySettings bSettings)
    {
        bool aCircle = aSettings.HasCircleFootprint;
        bool bCircle = bSettings.HasCircleFootprint;
        if (aCircle && bCircle)
        {
            return Vector2.Distance(ToXZ(aPosition), ToXZ(bPosition)) <= aSettings.Radius + bSettings.Radius;
        }

        if (aCircle)
        {
            return CircleIntersectsRect(aPosition, aSettings.Radius, bPosition, bRotation, bSettings.Width, bSettings.Length);
        }

        if (bCircle)
        {
            return CircleIntersectsRect(bPosition, bSettings.Radius, aPosition, aRotation, aSettings.Width, aSettings.Length);
        }

        return RectIntersectsRect(
            aPosition,
            aRotation,
            aSettings.Width,
            aSettings.Length,
            bPosition,
            bRotation,
            bSettings.Width,
            bSettings.Length);
    }

    private static bool CircleIntersectsRect(
        Vector3 circlePosition,
        float radius,
        Vector3 rectPosition,
        Quaternion rectRotation,
        float rectWidth,
        float rectLength)
    {
        Vector3 local = InverseYaw(rectRotation) * (circlePosition - rectPosition);
        float x = Mathf.Clamp(local.x, rectWidth * -0.5f, rectWidth * 0.5f);
        float z = Mathf.Clamp(local.z, rectLength * -0.5f, rectLength * 0.5f);
        float dx = local.x - x;
        float dz = local.z - z;
        return dx * dx + dz * dz <= radius * radius;
    }

    private static bool RectIntersectsRect(
        Vector3 aPosition,
        Quaternion aRotation,
        float aWidth,
        float aLength,
        Vector3 bPosition,
        Quaternion bRotation,
        float bWidth,
        float bLength)
    {
        Vector2 aCenter = ToXZ(aPosition);
        Vector2 bCenter = ToXZ(bPosition);
        GetRectAxes(aRotation, out Vector2 aX, out Vector2 aZ);
        GetRectAxes(bRotation, out Vector2 bX, out Vector2 bZ);
        Vector2 delta = bCenter - aCenter;

        return OverlapsOnAxis(aX, delta, aX, aZ, aWidth * 0.5f, aLength * 0.5f, bX, bZ, bWidth * 0.5f, bLength * 0.5f) &&
               OverlapsOnAxis(aZ, delta, aX, aZ, aWidth * 0.5f, aLength * 0.5f, bX, bZ, bWidth * 0.5f, bLength * 0.5f) &&
               OverlapsOnAxis(bX, delta, aX, aZ, aWidth * 0.5f, aLength * 0.5f, bX, bZ, bWidth * 0.5f, bLength * 0.5f) &&
               OverlapsOnAxis(bZ, delta, aX, aZ, aWidth * 0.5f, aLength * 0.5f, bX, bZ, bWidth * 0.5f, bLength * 0.5f);
    }

    private static bool OverlapsOnAxis(
        Vector2 axis,
        Vector2 centerDelta,
        Vector2 aX,
        Vector2 aZ,
        float aHalfWidth,
        float aHalfLength,
        Vector2 bX,
        Vector2 bZ,
        float bHalfWidth,
        float bHalfLength)
    {
        float distance = Mathf.Abs(Vector2.Dot(centerDelta, axis));
        float aProjection = aHalfWidth * Mathf.Abs(Vector2.Dot(aX, axis)) + aHalfLength * Mathf.Abs(Vector2.Dot(aZ, axis));
        float bProjection = bHalfWidth * Mathf.Abs(Vector2.Dot(bX, axis)) + bHalfLength * Mathf.Abs(Vector2.Dot(bZ, axis));
        return distance <= aProjection + bProjection;
    }

    private static void GetRectAxes(Quaternion rotation, out Vector2 xAxis, out Vector2 zAxis)
    {
        Quaternion yaw = Yaw(rotation);
        Vector3 right = yaw * Vector3.right;
        Vector3 forward = yaw * Vector3.forward;
        xAxis = ToXZ(right).normalized;
        zAxis = ToXZ(forward).normalized;
    }

    private static Quaternion InverseYaw(Quaternion rotation)
    {
        return Quaternion.Inverse(Yaw(rotation));
    }

    private static Quaternion Yaw(Quaternion rotation)
    {
        return Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
    }

    private static Vector2 ToXZ(Vector3 value)
    {
        return new Vector2(value.x, value.z);
    }

    private static bool ShouldShowTo(Player player)
    {
        return player != null &&
               player == Player.m_localPlayer &&
               IsDebugModeEnabled() &&
               IsAdmin();
    }

    private static bool IsDebugModeEnabled()
    {
        return Player.m_debugMode;
    }

    private static bool IsAdmin()
    {
        return ZNet.instance != null && ZNet.instance.LocalPlayerIsAdminOrHost();
    }

    private static bool HasStoredSettings(ZDO zdo)
    {
        return zdo.GetInt(VersionHash, 0) == DataVersion;
    }

    private static void WriteCurrentSettings(ZDO zdo)
    {
        WriteSettings(zdo, CurrentSettings());
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
        int defaultMode = zdo.GetPrefab() == SlopePrefabHash
            ? (int)TerrainProxyMode.Slope
            : zdo.GetPrefab() == PaintPrefabHash
                ? (int)TerrainProxyMode.Paint
                : (int)TerrainProxyMode.Circle;
        TerrainProxyMode mode = (TerrainProxyMode)zdo.GetInt(
            ModeHash,
            defaultMode);

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

    private static bool HasAppliedAtPosition(ZDO zdo, Vector3 position)
    {
        if (!zdo.GetVec3(AppliedPositionHash, out Vector3 appliedPosition))
        {
            return false;
        }

        return Utils.DistanceXZ(appliedPosition, position) < 0.1f &&
               Mathf.Abs(appliedPosition.y - position.y) < 0.1f;
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

    private enum TerrainProxyMode
    {
        Circle,
        Slope,
        Paint
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
        public bool HasCircleFootprint => Mode != TerrainProxyMode.Slope;

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
    private Coroutine? _destroyRoutine;

    private void Awake()
    {
        AdminTerrainTool.ApplyLoadedProxy(this);
    }

    public void OnPlaced()
    {
        AdminTerrainTool.ConfigurePlacedProxy(this);
    }

    public void QueueDestroy()
    {
        if (_destroyRoutine != null)
        {
            return;
        }

        _destroyRoutine = StartCoroutine(DestroyNextFrame());
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
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class AdminTerrainToolScenePatch
{
    private static void Postfix()
    {
        AdminTerrainTool.Update();
    }
}

[HarmonyPatch(typeof(ZNetScene), "CreateObject")]
internal static class AdminTerrainToolCreateObjectPatch
{
    private static bool Prefix(ZDO zdo, ref GameObject __result)
    {
        return !AdminTerrainTool.TryCreateLoadedProxy(zdo, ref __result);
    }
}

[HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
internal static class AdminTerrainToolPieceTablePatch
{
    private static void Postfix(PieceTable __instance, Player player)
    {
        AdminTerrainTool.AddToAvailablePieces(__instance, player);
    }
}

[HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
internal static class AdminTerrainToolPlacementGhostPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Player __instance)
    {
        AdminTerrainTool.PreparePlacementGhost(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacement))]
internal static class AdminTerrainToolPlacementWheelPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo original = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel));
        MethodInfo replacement = AccessTools.Method(typeof(AdminTerrainTool), nameof(AdminTerrainTool.GetPlacementMouseScrollWheel));
        if (original == null || replacement == null)
        {
            return instructions;
        }

        return ReplaceMouseScrollWheel(instructions, original, replacement);
    }

    private static IEnumerable<CodeInstruction> ReplaceMouseScrollWheel(
        IEnumerable<CodeInstruction> instructions,
        MethodInfo original,
        MethodInfo replacement)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                instruction.operand = replacement;
            }

            yield return instruction;
        }
    }
}

[HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.ApplyToHeightmap))]
internal static class AdminTerrainToolTerrainCompPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo limit = AccessTools.Method(typeof(AdminTerrainTool), nameof(AdminTerrainTool.GetTerrainCompHeightClampLimit));
        if (limit == null)
        {
            return instructions;
        }

        return ReplaceVanillaTerrainCompClamp(instructions, limit);
    }

    private static IEnumerable<CodeInstruction> ReplaceVanillaTerrainCompClamp(IEnumerable<CodeInstruction> instructions, MethodInfo limit)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldc_R4 &&
                instruction.operand is float value &&
                Math.Abs(value - 8f) < 0.001f)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = limit;
            }

            yield return instruction;
        }
    }
}

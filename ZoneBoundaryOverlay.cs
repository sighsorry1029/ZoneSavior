using System.Linq;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneBoundaryOverlay
{
    private const int SegmentsPerEdge = 16;
    private const float LineHeightOffset = 0.12f;

    private static bool _visible;
    private static GameObject? _lineRoot;
    private static LineRenderer? _line;
    private static Material? _lineMaterial;
    private static TextMeshProUGUI? _hudText;
    private static CanvasGroup? _hudGroup;
    private static Vector2i _lastZone = new(int.MinValue, int.MinValue);
    private static float _nextRefreshTime;

    public static void Update()
    {
        if (IsToggleHotkeyDown())
        {
            _visible = !_visible;
            _lastZone = new Vector2i(int.MinValue, int.MinValue);
            SetVisible(_visible);
        }

        if (!_visible)
        {
            return;
        }

        Player player = Player.m_localPlayer;
        if (player == null || player.IsDead() || ZoneSystem.instance == null)
        {
            SetVisible(false);
            return;
        }

        EnsureHud();
        EnsureLine();

        Vector2i zone = ZoneSystem.GetZone(player.transform.position);
        UpdateHud(zone);

        if (zone.x != _lastZone.x || zone.y != _lastZone.y || Time.time >= _nextRefreshTime)
        {
            DrawBoundary(zone);
            _lastZone = zone;
            _nextRefreshTime = Time.time + 0.5f;
        }
    }

    public static void Shutdown()
    {
        _visible = false;
        if (_lineRoot != null)
        {
            Object.Destroy(_lineRoot);
        }

        if (_hudText != null)
        {
            Object.Destroy(_hudText.gameObject);
        }

        _lineRoot = null;
        _line = null;
        _lineMaterial = null;
        _hudText = null;
        _hudGroup = null;
        _lastZone = new Vector2i(int.MinValue, int.MinValue);
    }

    private static bool IsToggleHotkeyDown()
    {
        if (ShouldBlockInput())
        {
            return false;
        }

        KeyboardShortcut shortcut = ClientConfig.ZoneUiToggleHotkey;
        if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
        {
            return false;
        }

        return shortcut.Modifiers.All(IsModifierHeld);
    }

    private static bool ShouldBlockInput()
    {
        return global::Console.IsVisible() ||
               ZoneSaviorInputBlockers.IsTextInputVisible() ||
               Menu.IsVisible();
    }

    private static bool IsModifierHeld(KeyCode key)
    {
        return key switch
        {
            KeyCode.LeftShift or KeyCode.RightShift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift),
            KeyCode.LeftControl or KeyCode.RightControl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl),
            KeyCode.LeftAlt or KeyCode.RightAlt => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt),
            KeyCode.None => true,
            _ => Input.GetKey(key)
        };
    }

    private static void SetVisible(bool visible)
    {
        if (_line != null)
        {
            _line.enabled = visible;
        }

        if (_hudGroup != null)
        {
            _hudGroup.alpha = visible ? 1f : 0f;
        }
    }

    private static void EnsureLine()
    {
        if (_line != null)
        {
            _line.enabled = _visible;
            return;
        }

        if (_lineMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader != null)
            {
                _lineMaterial = new Material(shader);
                _lineMaterial.color = Color.white;
            }
        }

        _lineRoot = new GameObject("ZoneSaviorZoneBoundary");
        Object.DontDestroyOnLoad(_lineRoot);
        _line = _lineRoot.AddComponent<LineRenderer>();
        _line.useWorldSpace = true;
        _line.startWidth = 0.18f;
        _line.endWidth = 0.18f;
        _line.numCornerVertices = 2;
        _line.numCapVertices = 2;
        _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _line.receiveShadows = false;
        _line.material = _lineMaterial;
        _line.startColor = new Color(0.2f, 0.85f, 1f, 0.95f);
        _line.endColor = new Color(0.2f, 0.85f, 1f, 0.95f);
        _line.enabled = _visible;
    }

    private static void EnsureHud()
    {
        if (_hudText != null && _hudGroup != null)
        {
            _hudGroup.alpha = _visible ? 1f : 0f;
            return;
        }

        if (Hud.instance == null)
        {
            return;
        }

        TextMeshProUGUI? template = FindHudTextTemplate();
        if (template?.font == null)
        {
            return;
        }

        _hudText = Object.Instantiate(template, Hud.instance.transform, false);
        _hudText.gameObject.name = "ZoneSaviorZoneHud";
        _hudText.gameObject.SetActive(true);
        _hudText.transform.SetAsLastSibling();
        _hudText.font = template.font;
        if (template.fontSharedMaterial != null)
        {
            _hudText.fontSharedMaterial = template.fontSharedMaterial;
        }

        _hudText.color = new Color(0.2f, 0.85f, 1f, 1f);
        _hudText.alignment = TextAlignmentOptions.Center;
        _hudText.enableAutoSizing = false;
        _hudText.fontSize = 20f;
        _hudText.textWrappingMode = TextWrappingModes.NoWrap;
        _hudText.overflowMode = TextOverflowModes.Overflow;
        _hudText.raycastTarget = false;

        RectTransform rect = (RectTransform)_hudText.transform;
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -96f);
        rect.sizeDelta = new Vector2(260f, 42f);

        _hudGroup = _hudText.GetComponent<CanvasGroup>();
        if (_hudGroup == null)
        {
            _hudGroup = _hudText.gameObject.AddComponent<CanvasGroup>();
        }

        _hudGroup.alpha = _visible ? 1f : 0f;
    }

    private static TextMeshProUGUI? FindHudTextTemplate()
    {
        TextMeshProUGUI? buildSelection = Hud.instance?.m_buildSelection as TextMeshProUGUI;
        if (buildSelection?.font != null)
        {
            return buildSelection;
        }

        return Hud.instance?.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true)
            .FirstOrDefault(text => text != null && text.font != null);
    }

    private static void UpdateHud(Vector2i zone)
    {
        if (_hudText == null)
        {
            return;
        }

        _hudText.text = $"Zone {zone.x}, {zone.y}";
    }

    private static void DrawBoundary(Vector2i zone)
    {
        if (_line == null)
        {
            return;
        }

        float zoneSize = ZoneSystem.instance != null ? ZoneSystem.instance.m_zoneSize : ZoneSystem.c_ZoneSize;
        float half = zoneSize * 0.5f;
        Vector3 center = ZoneSystem.GetZonePos(zone);
        int pointCount = SegmentsPerEdge * 4 + 1;
        _line.positionCount = pointCount;

        int index = 0;
        AddEdge(ref index, center.x - half, center.z - half, center.x + half, center.z - half);
        AddEdge(ref index, center.x + half, center.z - half, center.x + half, center.z + half);
        AddEdge(ref index, center.x + half, center.z + half, center.x - half, center.z + half);
        AddEdge(ref index, center.x - half, center.z + half, center.x - half, center.z - half);

        Vector3 first = _line.GetPosition(0);
        _line.SetPosition(pointCount - 1, first);
    }

    private static void AddEdge(ref int index, float startX, float startZ, float endX, float endZ)
    {
        if (_line == null)
        {
            return;
        }

        for (int i = 0; i < SegmentsPerEdge; i++)
        {
            float t = i / (float)SegmentsPerEdge;
            float x = Mathf.Lerp(startX, endX, t);
            float z = Mathf.Lerp(startZ, endZ, t);
            float y = ZoneToolAim.SampleGroundY(x, z, 0f) + LineHeightOffset;
            _line.SetPosition(index++, new Vector3(x, y, z));
        }
    }
}


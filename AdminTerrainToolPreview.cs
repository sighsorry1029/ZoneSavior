using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    private static void HideAreaLine()
    {
        if (_areaLine)
        {
            _areaLine.enabled = false;
        }

        _lastPreviewKind = PreviewKind.None;
    }

    private static void EnsureAreaLine()
    {
        if (_areaLine)
        {
            return;
        }

        _previewRoot = new GameObject(AreaLineName);
        UnityEngine.Object.DontDestroyOnLoad(_previewRoot);
        _lineMaterial ??= CreateLineMaterial();
        _areaLine = _previewRoot.AddComponent<LineRenderer>();
        _areaLine.useWorldSpace = true;
        _areaLine.startWidth = AreaLineWidth;
        _areaLine.endWidth = AreaLineWidth;
        _areaLine.numCornerVertices = 2;
        _areaLine.numCapVertices = 2;
        _areaLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _areaLine.receiveShadows = false;
        _areaLine.material = _lineMaterial;
        _areaLine.startColor = AreaColor();
        _areaLine.endColor = AreaColor();
        _areaLine.enabled = false;
    }

    private static Material? CreateLineMaterial()
    {
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (!shader)
        {
            return null;
        }

        return new Material(shader)
        {
            color = Color.white
        };
    }

    private static void DrawCircleLineIfChanged(Vector3 center, Quaternion rotation, float radius)
    {
        if (PreviewUnchanged(PreviewKind.Circle, center, rotation.eulerAngles.y, radius, 0f, 0f, 0f))
        {
            return;
        }

        DrawCircleLine(center, rotation, radius);
    }

    private static void DrawSlopeAreaLineIfChanged(SlopePlacement placement)
    {
        if (PreviewUnchanged(
                PreviewKind.Slope,
                placement.Center,
                placement.Rotation.eulerAngles.y,
                0f,
                placement.Width,
                placement.Length,
                placement.HeightDelta))
        {
            return;
        }

        DrawSlopeAreaLine(placement);
    }

    private static void DrawWidthLineIfChanged(Vector3 center, Quaternion rotation, float width)
    {
        if (PreviewUnchanged(PreviewKind.Width, center, rotation.eulerAngles.y, 0f, width, 0f, 0f))
        {
            return;
        }

        DrawWidthLine(center, rotation, width);
    }

    private static bool PreviewUnchanged(
        PreviewKind kind,
        Vector3 position,
        float yaw,
        float radius,
        float width,
        float length,
        float heightDelta)
    {
        if (_areaLine &&
            _areaLine.enabled &&
            _lastPreviewKind == kind &&
            (_lastPreviewPosition - position).sqrMagnitude <= PreviewPositionEpsilon * PreviewPositionEpsilon &&
            Mathf.Abs(Mathf.DeltaAngle(_lastPreviewYaw, yaw)) <= PreviewFloatEpsilon &&
            Mathf.Abs(_lastPreviewRadius - radius) <= PreviewFloatEpsilon &&
            Mathf.Abs(_lastPreviewWidth - width) <= PreviewFloatEpsilon &&
            Mathf.Abs(_lastPreviewLength - length) <= PreviewFloatEpsilon &&
            Mathf.Abs(_lastPreviewHeightDelta - heightDelta) <= PreviewFloatEpsilon)
        {
            return true;
        }

        _lastPreviewKind = kind;
        _lastPreviewPosition = position;
        _lastPreviewYaw = yaw;
        _lastPreviewRadius = radius;
        _lastPreviewWidth = width;
        _lastPreviewLength = length;
        _lastPreviewHeightDelta = heightDelta;
        return false;
    }

    private static void DrawCircleLine(Vector3 center, Quaternion rotation, float radius)
    {
        EnsureAreaLine();
        if (!_areaLine)
        {
            return;
        }

        _areaLine.enabled = true;
        Color color = AreaColor();
        _areaLine.startColor = color;
        _areaLine.endColor = color;

        const int segments = 96;
        _areaLine.positionCount = segments + 1;
        for (int i = 0; i <= segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            Vector3 point = center + rotation * new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            _areaLine.SetPosition(i, point);
        }
    }

    private static void DrawWidthLine(Vector3 center, Quaternion rotation, float width)
    {
        EnsureAreaLine();
        if (!_areaLine)
        {
            return;
        }

        _areaLine.enabled = true;
        Color color = AreaColor();
        _areaLine.startColor = color;
        _areaLine.endColor = color;

        float halfWidth = width * 0.5f;
        _areaLine.positionCount = 2;
        _areaLine.SetPosition(0, center + rotation * new Vector3(-halfWidth, 0f, 0f));
        _areaLine.SetPosition(1, center + rotation * new Vector3(halfWidth, 0f, 0f));
    }

    private static void DrawSlopeAreaLine(SlopePlacement placement)
    {
        EnsureAreaLine();
        if (!_areaLine)
        {
            return;
        }

        _areaLine.enabled = true;
        Color color = AreaColor();
        _areaLine.startColor = color;
        _areaLine.endColor = color;

        float halfWidth = placement.Width * 0.5f;
        float halfLength = placement.Length * 0.5f;
        _areaLine.positionCount = 5;
        _areaLine.SetPosition(0, placement.GetWorldPoint(-halfWidth, -halfLength));
        _areaLine.SetPosition(1, placement.GetWorldPoint(-halfWidth, halfLength));
        _areaLine.SetPosition(2, placement.GetWorldPoint(halfWidth, halfLength));
        _areaLine.SetPosition(3, placement.GetWorldPoint(halfWidth, -halfLength));
        _areaLine.SetPosition(4, placement.GetWorldPoint(-halfWidth, -halfLength));
    }

    private static Color AreaColor()
    {
        return new Color(1f, 0.05f, 0.85f, 1f);
    }

    private enum PreviewKind
    {
        None,
        Circle,
        Width,
        Slope
    }
}

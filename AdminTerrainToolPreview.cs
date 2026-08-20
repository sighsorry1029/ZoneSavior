using System.Collections.Generic;
using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    private const int PaintGridPreviewMaxMarkers = 2048;
    private const float PaintGridPreviewMarkerSize = 0.16f;
    private const float PaintGridPreviewYOffset = 0.06f;
    private const int PaintGridPreviewRefreshFrames = 30;
    private const string PaintGridPreviewName = "ZoneSaviorPaintProxyGrid";
    private const int TerrainCirclePreviewMinSegments = 96;
    private const int TerrainCirclePreviewMaxSegments = 768;
    private const float TerrainCirclePreviewSegmentLength = 1f;
    private const float TerrainCirclePreviewYOffset = 0.06f;
    private const float TerrainCirclePreviewGridEpsilon = 0.001f;
    private const int TerrainCirclePreviewRefreshFrames = 30;

    private static readonly List<Heightmap> PaintGridPreviewHeightmaps = [];
    private static readonly HashSet<Vector2Int> PaintGridPreviewWorldPoints = [];
    private static readonly List<Vector3> PaintGridPreviewNodes = [];
    private static readonly List<Vector3> PaintGridPreviewVertices = [];
    private static readonly List<int> PaintGridPreviewIndices = [];
    private static readonly List<Color> PaintGridPreviewColors = [];
    private static readonly List<Heightmap> TerrainCirclePreviewHeightmaps = [];
    private static readonly List<Vector3> TerrainCirclePreviewPositions = new(TerrainCirclePreviewMaxSegments + 1);
    private static GameObject? _paintGridPreviewObject;
    private static MeshRenderer? _paintGridPreviewRenderer;
    private static Mesh? _paintGridPreviewMesh;
    private static int _paintGridPreviewSignature;
    private static bool _paintGridPreviewSignatureValid;
    private static int _nextPaintGridPreviewRefreshFrame;
    private static int _nextTerrainCirclePreviewRefreshFrame;

    private static void HideAreaLine()
    {
        if (_areaLine)
        {
            _areaLine.enabled = false;
        }

        HidePaintGridPreview();
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

    private static void DrawTerrainCircleLineIfChanged(Vector3 center, Quaternion rotation, float radius)
    {
        bool requestUnchanged =
            IsPreviewVisible(PreviewKind.TerrainCircle) &&
            _lastPreviewKind == PreviewKind.TerrainCircle &&
            _lastPreviewPosition.x == center.x &&
            _lastPreviewPosition.y == center.y &&
            _lastPreviewPosition.z == center.z &&
            _lastPreviewYaw == rotation.eulerAngles.y &&
            _lastPreviewRadius == radius;
        if (requestUnchanged && Time.frameCount < _nextTerrainCirclePreviewRefreshFrame)
        {
            return;
        }

        _ = PreviewUnchanged(
            PreviewKind.TerrainCircle,
            center,
            rotation.eulerAngles.y,
            radius,
            0f,
            0f,
            0f);
        _lastPreviewPosition = center;
        _lastPreviewYaw = rotation.eulerAngles.y;
        _lastPreviewRadius = radius;
        DrawTerrainCircleLine(center, rotation, radius);
        _nextTerrainCirclePreviewRefreshFrame = Time.frameCount + TerrainCirclePreviewRefreshFrames;
    }

    private static void DrawPaintGridPreviewIfChanged(
        Vector3 center,
        Quaternion rotation,
        TerrainProxySettings settings,
        bool includeBoundary = false)
    {
        PreviewKind previewKind = includeBoundary
            ? PreviewKind.PaintOnlyResetGrid
            : PreviewKind.PaintGrid;
        bool requestUnchanged =
            _lastPreviewKind == previewKind &&
            _lastPreviewPosition.x == center.x &&
            _lastPreviewPosition.y == center.y &&
            _lastPreviewPosition.z == center.z &&
            _lastPreviewRadius == settings.Radius;
        if (requestUnchanged && Time.frameCount < _nextPaintGridPreviewRefreshFrame)
        {
            return;
        }

        _ = PreviewUnchanged(
            previewKind,
            center,
            rotation.eulerAngles.y,
            settings.Radius,
            0f,
            0f,
            0f);
        _lastPreviewPosition = center;
        _lastPreviewYaw = rotation.eulerAngles.y;
        _lastPreviewRadius = settings.Radius;
        DrawTerrainCircleLine(center, rotation, settings.Radius, hidePaintGrid: false);

        if (!TryCollectPaintGridPreviewNodes(
                center,
                rotation,
                settings,
                includeBoundary,
                out int signature))
        {
            HidePaintGridPreview();
            _nextPaintGridPreviewRefreshFrame = Time.frameCount + PaintGridPreviewRefreshFrames;
            return;
        }

        if (_paintGridPreviewObject &&
            _paintGridPreviewObject.activeSelf &&
            _paintGridPreviewSignatureValid &&
            _paintGridPreviewSignature == signature)
        {
            _nextPaintGridPreviewRefreshFrame = Time.frameCount + PaintGridPreviewRefreshFrames;
            return;
        }

        if (!TryDrawPaintGridPreview())
        {
            HidePaintGridPreview();
            _nextPaintGridPreviewRefreshFrame = Time.frameCount + PaintGridPreviewRefreshFrames;
            return;
        }

        _paintGridPreviewSignature = signature;
        _paintGridPreviewSignatureValid = true;
        _nextPaintGridPreviewRefreshFrame = Time.frameCount + PaintGridPreviewRefreshFrames;
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
        if (IsPreviewVisible(kind) &&
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

    private static bool IsPreviewVisible(PreviewKind kind)
    {
        if (kind is PreviewKind.PaintGrid or PreviewKind.PaintOnlyResetGrid)
        {
            return _paintGridPreviewObject && _paintGridPreviewObject.activeSelf;
        }

        return _areaLine && _areaLine.enabled;
    }

    private static void DrawCircleLine(
        Vector3 center,
        Quaternion rotation,
        float radius,
        bool hidePaintGrid = true)
    {
        EnsureAreaLine();
        if (!_areaLine)
        {
            return;
        }

        if (hidePaintGrid)
        {
            HidePaintGridPreview();
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

    private static void DrawTerrainCircleLine(
        Vector3 center,
        Quaternion rotation,
        float radius,
        bool hidePaintGrid = true)
    {
        if (!TryCollectTerrainCirclePreviewPositions(center, rotation, radius))
        {
            DrawCircleLine(center, rotation, radius, hidePaintGrid);
            return;
        }

        EnsureAreaLine();
        if (!_areaLine)
        {
            return;
        }

        if (hidePaintGrid)
        {
            HidePaintGridPreview();
        }

        _areaLine.enabled = true;
        Color color = AreaColor();
        _areaLine.startColor = color;
        _areaLine.endColor = color;
        _areaLine.positionCount = TerrainCirclePreviewPositions.Count;
        for (int index = 0; index < TerrainCirclePreviewPositions.Count; index++)
        {
            _areaLine.SetPosition(index, TerrainCirclePreviewPositions[index]);
        }
    }

    private static bool TryCollectTerrainCirclePreviewPositions(
        Vector3 center,
        Quaternion rotation,
        float radius)
    {
        TerrainCirclePreviewPositions.Clear();
        TerrainCirclePreviewHeightmaps.Clear();
        Heightmap.FindHeightmap(
            center,
            radius + SearchPadding,
            TerrainCirclePreviewHeightmaps);
        TerrainCirclePreviewHeightmaps.RemoveAll(heightmap =>
            !IsPreviewHeightmapReady(heightmap));
        if (TerrainCirclePreviewHeightmaps.Count == 0)
        {
            return false;
        }

        int segments = Mathf.Clamp(
            Mathf.CeilToInt(Mathf.PI * 2f * radius / TerrainCirclePreviewSegmentLength),
            TerrainCirclePreviewMinSegments,
            TerrainCirclePreviewMaxSegments);
        for (int index = 0; index < segments; index++)
        {
            float angle = index * Mathf.PI * 2f / segments;
            Vector3 point = center + rotation * new Vector3(
                Mathf.Cos(angle) * radius,
                0f,
                Mathf.Sin(angle) * radius);
            if (!TryGetTerrainCircleSurfaceHeight(point, out float terrainHeight))
            {
                TerrainCirclePreviewHeightmaps.Clear();
                TerrainCirclePreviewPositions.Clear();
                return false;
            }

            point.y = terrainHeight + TerrainCirclePreviewYOffset;
            TerrainCirclePreviewPositions.Add(point);
        }

        TerrainCirclePreviewPositions.Add(TerrainCirclePreviewPositions[0]);
        TerrainCirclePreviewHeightmaps.Clear();
        return true;
    }

    private static bool IsPreviewHeightmapReady(Heightmap heightmap)
    {
        if (!heightmap ||
            !heightmap.isActiveAndEnabled ||
            heightmap.IsDistantLod ||
            heightmap.m_width <= 0 ||
            heightmap.m_scale <= 0f ||
            float.IsNaN(heightmap.m_scale) ||
            float.IsInfinity(heightmap.m_scale))
        {
            return false;
        }

        int vertexWidth = heightmap.m_width + 1;
        return heightmap.m_heights != null &&
               heightmap.m_heights.Count == vertexWidth * vertexWidth;
    }

    private static bool TryGetTerrainCircleSurfaceHeight(Vector3 point, out float height)
    {
        Heightmap selectedHeightmap = null!;
        Vector3 selectedLocalPoint = default;
        float selectedGridX = 0f;
        float selectedGridZ = 0f;
        float selectedInteriorMargin = float.NegativeInfinity;
        foreach (Heightmap heightmap in TerrainCirclePreviewHeightmaps)
        {
            Vector3 localPoint = heightmap.transform.InverseTransformPoint(point);
            float halfWidth = heightmap.m_width * heightmap.m_scale * 0.5f;
            float gridX = (localPoint.x + halfWidth) / heightmap.m_scale;
            float gridZ = (localPoint.z + halfWidth) / heightmap.m_scale;
            if (gridX < -TerrainCirclePreviewGridEpsilon ||
                gridZ < -TerrainCirclePreviewGridEpsilon ||
                gridX > heightmap.m_width + TerrainCirclePreviewGridEpsilon ||
                gridZ > heightmap.m_width + TerrainCirclePreviewGridEpsilon)
            {
                continue;
            }

            gridX = Mathf.Clamp(gridX, 0f, heightmap.m_width);
            gridZ = Mathf.Clamp(gridZ, 0f, heightmap.m_width);
            float interiorMargin = Mathf.Min(
                Mathf.Min(gridX, heightmap.m_width - gridX),
                Mathf.Min(gridZ, heightmap.m_width - gridZ));
            if (selectedHeightmap && interiorMargin <= selectedInteriorMargin)
            {
                continue;
            }

            selectedHeightmap = heightmap;
            selectedLocalPoint = localPoint;
            selectedGridX = gridX;
            selectedGridZ = gridZ;
            selectedInteriorMargin = interiorMargin;
        }

        if (!selectedHeightmap)
        {
            height = 0f;
            return false;
        }

        int cellX = Mathf.Min(Mathf.FloorToInt(selectedGridX), selectedHeightmap.m_width - 1);
        int cellZ = Mathf.Min(Mathf.FloorToInt(selectedGridZ), selectedHeightmap.m_width - 1);
        float cellXFactor = selectedGridX - cellX;
        float cellZFactor = selectedGridZ - cellZ;
        float height00 = selectedHeightmap.GetHeight(cellX, cellZ);
        float height10 = selectedHeightmap.GetHeight(cellX + 1, cellZ);
        float height01 = selectedHeightmap.GetHeight(cellX, cellZ + 1);
        float height11 = selectedHeightmap.GetHeight(cellX + 1, cellZ + 1);
        // Match Heightmap.RebuildCollisionMesh's v00-v01-v10 / v10-v01-v11 diagonal.
        float localHeight = cellXFactor + cellZFactor <= 1f
            ? height00 + (height10 - height00) * cellXFactor +
              (height01 - height00) * cellZFactor
            : height11 + (height01 - height11) * (1f - cellXFactor) +
              (height10 - height11) * (1f - cellZFactor);
        height = selectedHeightmap.transform.TransformPoint(
            new Vector3(selectedLocalPoint.x, localHeight, selectedLocalPoint.z)).y;
        return true;
    }

    private static void DrawWidthLine(Vector3 center, Quaternion rotation, float width)
    {
        EnsureAreaLine();
        if (!_areaLine)
        {
            return;
        }

        HidePaintGridPreview();
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

        HidePaintGridPreview();
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

    private static bool TryCollectPaintGridPreviewNodes(
        Vector3 center,
        Quaternion rotation,
        TerrainProxySettings settings,
        bool includeBoundary,
        out int signature)
    {
        PaintGridPreviewNodes.Clear();
        PaintGridPreviewWorldPoints.Clear();
        PaintGridPreviewHeightmaps.Clear();
        Heightmap.FindHeightmap(center, settings.SearchRadius + SearchPadding, PaintGridPreviewHeightmaps);
        PaintGridPreviewHeightmaps.RemoveAll(heightmap => !IsPreviewHeightmapReady(heightmap));
        if (!IsFootprintCoveredByHeightmaps(
                center,
                rotation,
                settings,
                PaintGridPreviewHeightmaps,
                PaintGridPreviewHeightmaps,
                includeCircleBoundary: includeBoundary))
        {
            signature = 0;
            return false;
        }

        Quaternion inverseYaw = InverseYaw(rotation);
        int hash = includeBoundary ? 19 : 17;
        foreach (Heightmap heightmap in PaintGridPreviewHeightmaps)
        {
            if (!TryGetHeightmapIndexRange(
                    heightmap,
                    center,
                    settings.SearchRadius + SearchPadding,
                    out int minX,
                    out int maxX,
                    out int minZ,
                    out int maxZ))
            {
                continue;
            }

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Vector3 node = VertexToWorld(heightmap, x, z);
                    if (!TryGetAffectedTerrainNode(
                            center,
                            inverseYaw,
                            settings,
                            node,
                            out _,
                            out float normalized) ||
                        (!includeBoundary && !HasPaintInfluence(normalized)))
                    {
                        continue;
                    }

                    Vector2Int worldPoint = new(
                        Mathf.RoundToInt(node.x * 1000f),
                        Mathf.RoundToInt(node.z * 1000f));
                    if (!PaintGridPreviewWorldPoints.Add(worldPoint))
                    {
                        continue;
                    }

                    if (PaintGridPreviewNodes.Count >= PaintGridPreviewMaxMarkers)
                    {
                        PaintGridPreviewNodes.Clear();
                        PaintGridPreviewWorldPoints.Clear();
                        signature = 0;
                        return false;
                    }

                    Vector3 marker = heightmap.transform.TransformPoint(heightmap.CalcVertex(x, z));
                    marker.y += PaintGridPreviewYOffset;
                    PaintGridPreviewNodes.Add(marker);
                    unchecked
                    {
                        hash = hash * 31 + worldPoint.x;
                        hash = hash * 31 + worldPoint.y;
                        hash = hash * 31 + Mathf.RoundToInt(marker.y * 100f);
                    }
                }
            }
        }

        signature = hash;
        return PaintGridPreviewNodes.Count > 0;
    }

    private static bool TryDrawPaintGridPreview()
    {
        if (!EnsurePaintGridPreview() || !_paintGridPreviewMesh || !_paintGridPreviewObject)
        {
            return false;
        }

        PaintGridPreviewVertices.Clear();
        PaintGridPreviewIndices.Clear();
        PaintGridPreviewColors.Clear();

        Color color = AreaColor();
        color.a = 0.42f;
        float halfSize = PaintGridPreviewMarkerSize * 0.5f;
        foreach (Vector3 center in PaintGridPreviewNodes)
        {
            int vertexStart = PaintGridPreviewVertices.Count;
            PaintGridPreviewVertices.Add(center + new Vector3(-halfSize, 0f, -halfSize));
            PaintGridPreviewVertices.Add(center + new Vector3(-halfSize, 0f, halfSize));
            PaintGridPreviewVertices.Add(center + new Vector3(halfSize, 0f, halfSize));
            PaintGridPreviewVertices.Add(center + new Vector3(halfSize, 0f, -halfSize));
            PaintGridPreviewColors.Add(color);
            PaintGridPreviewColors.Add(color);
            PaintGridPreviewColors.Add(color);
            PaintGridPreviewColors.Add(color);
            PaintGridPreviewIndices.Add(vertexStart);
            PaintGridPreviewIndices.Add(vertexStart + 1);
            PaintGridPreviewIndices.Add(vertexStart + 2);
            PaintGridPreviewIndices.Add(vertexStart);
            PaintGridPreviewIndices.Add(vertexStart + 2);
            PaintGridPreviewIndices.Add(vertexStart + 3);
        }

        _paintGridPreviewMesh.Clear();
        _paintGridPreviewMesh.SetVertices(PaintGridPreviewVertices);
        _paintGridPreviewMesh.SetColors(PaintGridPreviewColors);
        _paintGridPreviewMesh.SetIndices(PaintGridPreviewIndices, MeshTopology.Triangles, 0);
        _paintGridPreviewMesh.RecalculateBounds();

        _paintGridPreviewObject.SetActive(true);
        return true;
    }

    private static bool EnsurePaintGridPreview()
    {
        if (_paintGridPreviewObject && _paintGridPreviewRenderer && _paintGridPreviewMesh)
        {
            return true;
        }

        EnsureAreaLine();
        if (!_previewRoot || !_lineMaterial)
        {
            return false;
        }

        _paintGridPreviewObject = new GameObject(PaintGridPreviewName)
        {
            hideFlags = HideFlags.DontSave
        };
        _paintGridPreviewObject.transform.SetParent(_previewRoot.transform, worldPositionStays: false);
        MeshFilter filter = _paintGridPreviewObject.AddComponent<MeshFilter>();
        _paintGridPreviewRenderer = _paintGridPreviewObject.AddComponent<MeshRenderer>();
        _paintGridPreviewRenderer.sharedMaterial = _lineMaterial;
        _paintGridPreviewRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _paintGridPreviewRenderer.receiveShadows = false;
        _paintGridPreviewMesh = new Mesh
        {
            name = PaintGridPreviewName + "Mesh",
            hideFlags = HideFlags.DontSave
        };
        _paintGridPreviewMesh.MarkDynamic();
        filter.sharedMesh = _paintGridPreviewMesh;
        _paintGridPreviewObject.SetActive(false);
        return true;
    }

    private static void HidePaintGridPreview()
    {
        if (_paintGridPreviewObject)
        {
            _paintGridPreviewObject.SetActive(false);
        }
    }

    private static void ReleasePaintGridPreviewResources()
    {
        if (_paintGridPreviewMesh)
        {
            UnityEngine.Object.Destroy(_paintGridPreviewMesh);
        }

        _paintGridPreviewObject = null;
        _paintGridPreviewRenderer = null;
        _paintGridPreviewMesh = null;
        _paintGridPreviewSignature = 0;
        _paintGridPreviewSignatureValid = false;
        _nextPaintGridPreviewRefreshFrame = 0;
        _nextTerrainCirclePreviewRefreshFrame = 0;
        PaintGridPreviewHeightmaps.Clear();
        PaintGridPreviewWorldPoints.Clear();
        PaintGridPreviewNodes.Clear();
        PaintGridPreviewVertices.Clear();
        PaintGridPreviewIndices.Clear();
        PaintGridPreviewColors.Clear();
        TerrainCirclePreviewHeightmaps.Clear();
        TerrainCirclePreviewPositions.Clear();
    }

    private static Color AreaColor()
    {
        return new Color(1f, 0.05f, 0.85f, 1f);
    }

    private enum PreviewKind
    {
        None,
        Circle,
        TerrainCircle,
        PaintGrid,
        PaintOnlyResetGrid,
        Width,
        Slope
    }

}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using static ZoneSavior.ZoneBundleTerrainGrid;

namespace ZoneSavior;

internal static partial class ZoneBundleTerrain
{
    public static void ApplyBaseLayer(Heightmap heightmap)
    {
        TerrainComp compiler = TerrainComp.FindTerrainCompiler(heightmap.transform.position);
        if (!IsCompilerReady(compiler))
        {
            return;
        }

        byte[] payload = compiler.m_nview.GetZDO().GetByteArray(SupportFillBaseLayerHash);
        if (payload == null || payload.Length == 0)
        {
            return;
        }

        if (!TryDeserializeBaseLayer(payload, heightmap, out int width, out float[] worldHeights, out Color[] paints) ||
            width != heightmap.m_width + 1 ||
            worldHeights.Length != heightmap.m_heights.Count ||
            (paints.Length != 0 && paints.Length != worldHeights.Length))
        {
            return;
        }

        float heightmapY = heightmap.transform.position.y;
        for (int i = 0; i < worldHeights.Length; i++)
        {
            heightmap.m_heights[i] = worldHeights[i] - heightmapY;
        }

        if (paints.Length == worldHeights.Length)
        {
            for (int z = 0; z < width; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    heightmap.m_paintMask.SetPixel(x, z, paints[z * width + x]);
                }
            }
        }
    }

    internal static void ResetSupportFillBaseLayer(IEnumerable? heightNodes, IEnumerable? paintNodes, Vector3 position, float radius)
    {
        Dictionary<TerrainComp, HashSet<int>> heightIndices = CollectTerrainNodeIndices(heightNodes);
        Dictionary<TerrainComp, HashSet<int>> paintIndices = CollectTerrainNodeIndices(paintNodes);
        if (heightIndices.Count == 0 && paintIndices.Count == 0)
        {
            return;
        }

        HashSet<TerrainComp> compilers = heightIndices.Keys.Concat(paintIndices.Keys).ToHashSet();
        int changedCompilers = 0;
        foreach (TerrainComp compiler in compilers)
        {
            if (!TryLoadSupportFillBaseLayer(compiler, out int width, out float[] worldHeights, out Color[] paints))
            {
                continue;
            }

            bool hasStoredPaints = paints.Length == width * width;
            Color[]? basePaints = hasStoredPaints ? TryGetBasePaints(compiler.m_hmap, width) : null;
            bool changed = ResetSupportFillHeights(compiler.m_hmap, width, worldHeights, heightIndices.TryGetValue(compiler, out HashSet<int> heightSet) ? heightSet : null);
            changed |= ResetSupportFillPaints(width, paints, basePaints, hasStoredPaints && paintIndices.TryGetValue(compiler, out HashSet<int> paintSet) ? paintSet : null);

            if (!changed)
            {
                continue;
            }

            if (IsSupportFillBaseLayerNative(compiler.m_hmap, width, worldHeights, paints, basePaints))
            {
                compiler.m_nview.GetZDO().Set(SupportFillBaseLayerHash, Array.Empty<byte>());
            }
            else
            {
                compiler.m_nview.GetZDO().Set(SupportFillBaseLayerHash, SerializeBaseLayer(compiler.m_hmap, width, worldHeights, paints));
            }

            PersistCompiler(compiler);
            changedCompilers++;
        }

        if (changedCompilers > 0)
        {
            ClutterSystem.instance?.ResetGrass(position, radius);
        }
    }

    private static bool ResetSupportFillHeights(Heightmap heightmap, int width, float[] worldHeights, IEnumerable<int>? heightSet)
    {
        if (heightSet == null)
        {
            return false;
        }

        bool changed = false;
        foreach (int index in heightSet)
        {
            if (!IsValidPayloadIndex(index, width, worldHeights.Length))
            {
                continue;
            }

            IndexToXZ(index, width, out int x, out int z);
            Vector3 node = VertexToWorld(heightmap, x, z);
            if (!TryGetTerrainBaseHeight(node.x, node.z, out float baseHeight) ||
                Mathf.Abs(worldHeights[index] - baseHeight) <= 0.01f)
            {
                continue;
            }

            worldHeights[index] = baseHeight;
            changed = true;
        }

        return changed;
    }

    private static bool ResetSupportFillPaints(int width, Color[] paints, Color[]? basePaints, IEnumerable<int>? paintSet)
    {
        if (paintSet == null || basePaints == null)
        {
            return false;
        }

        bool changed = false;
        foreach (int index in paintSet)
        {
            if (!IsValidPayloadIndex(index, width, paints.Length) || index >= basePaints.Length)
            {
                continue;
            }

            Color basePaint = basePaints[index];
            if (Approximately(paints[index], basePaint))
            {
                continue;
            }

            paints[index] = basePaint;
            changed = true;
        }

        return changed;
    }

    private static bool TryLoadSupportFillBaseLayer(TerrainComp compiler, out int width, out float[] worldHeights, out Color[] paints)
    {
        width = 0;
        worldHeights = [];
        paints = [];
        if (!IsCompilerReady(compiler))
        {
            return false;
        }

        byte[] payload = compiler.m_nview.GetZDO().GetByteArray(SupportFillBaseLayerHash);
        return payload != null &&
               payload.Length > 0 &&
               TryDeserializeBaseLayer(payload, compiler.m_hmap, out width, out worldHeights, out paints) &&
               width == compiler.m_hmap.m_width + 1 &&
               worldHeights.Length == width * width &&
               (paints.Length == 0 || paints.Length == worldHeights.Length);
    }

    private static Dictionary<TerrainComp, HashSet<int>> CollectTerrainNodeIndices(IEnumerable? nodes)
    {
        Dictionary<TerrainComp, HashSet<int>> result = [];
        if (nodes == null)
        {
            return result;
        }

        foreach (object? node in nodes)
        {
            if (!TryReadTerrainNode(node, out TerrainComp compiler, out int index))
            {
                continue;
            }

            if (!result.TryGetValue(compiler, out HashSet<int> indices))
            {
                indices = [];
                result[compiler] = indices;
            }

            indices.Add(index);
        }

        return result;
    }

    private static bool TryReadTerrainNode(object? node, out TerrainComp compiler, out int index)
    {
        compiler = null!;
        index = -1;
        if (node == null)
        {
            return false;
        }

        Type type = node.GetType();
        FieldInfo? compilerField = AccessTools.Field(type, "Compiler");
        FieldInfo? indexField = AccessTools.Field(type, "Index");
        TerrainComp? nodeCompiler = compilerField?.GetValue(node) as TerrainComp;
        object? indexValue = indexField?.GetValue(node);
        if (nodeCompiler == null || indexValue is not int nodeIndex)
        {
            return false;
        }

        compiler = nodeCompiler;
        index = nodeIndex;
        return true;
    }

    private static Color[]? TryGetBasePaints(Heightmap heightmap, int width)
    {
        if (HeightmapBuilder.instance == null || WorldGenerator.instance == null)
        {
            return null;
        }

        try
        {
            HeightmapBuilder.HMBuildData data = HeightmapBuilder.instance.RequestTerrainSync(
                heightmap.transform.position,
                heightmap.m_width,
                heightmap.m_scale,
                heightmap.IsDistantLod,
                WorldGenerator.instance);
            return data.m_baseMask != null && data.m_baseMask.Length == width * width ? data.m_baseMask : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportFillBaseLayerNative(Heightmap heightmap, int width, float[] worldHeights, Color[] paints, Color[]? basePaints)
    {
        bool hasStoredPaints = paints.Length == width * width;
        if (hasStoredPaints && (basePaints == null || basePaints.Length != paints.Length))
        {
            return false;
        }

        for (int index = 0; index < worldHeights.Length; index++)
        {
            IndexToXZ(index, width, out int x, out int z);
            Vector3 node = VertexToWorld(heightmap, x, z);
            if (!TryGetTerrainBaseHeight(node.x, node.z, out float baseHeight) ||
                Mathf.Abs(worldHeights[index] - baseHeight) > 0.01f ||
                (hasStoredPaints && !Approximately(paints[index], basePaints![index])))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Approximately(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.001f &&
               Mathf.Abs(a.g - b.g) < 0.001f &&
               Mathf.Abs(a.b - b.b) < 0.001f &&
               Mathf.Abs(a.a - b.a) < 0.001f;
    }

    private static byte[] SerializeBaseLayer(Heightmap heightmap, int width, float[] worldHeights, Color[] paints)
    {
        Color[] paintPayload = ShouldSerializePaints(heightmap, width, paints) ? paints : [];
        return SerializeSparseBaseLayer(heightmap, width, worldHeights, paintPayload);
    }

    private static byte[] SerializeSparseBaseLayer(Heightmap heightmap, int width, float[] worldHeights, Color[] paints)
    {
        List<int> heightIndices = [];
        List<float> heightValues = [];
        for (int index = 0; index < worldHeights.Length; index++)
        {
            IndexToXZ(index, width, out int x, out int z);
            float baseHeight = GetTerrainBaseOrCurrentHeight(heightmap, x, z);
            if (Mathf.Abs(worldHeights[index] - baseHeight) <= 0.01f)
            {
                continue;
            }

            heightIndices.Add(index);
            heightValues.Add(worldHeights[index]);
        }

        List<int> paintIndices = [];
        List<Color> paintValues = [];
        if (paints.Length == worldHeights.Length)
        {
            Color[]? basePaints = TryGetBasePaints(heightmap, width);
            for (int index = 0; index < paints.Length; index++)
            {
                Color basePaint = basePaints != null && basePaints.Length == paints.Length
                    ? basePaints[index]
                    : GetPaint(heightmap, index % width, index / width);
                if (Approximately(paints[index], basePaint))
                {
                    continue;
                }

                paintIndices.Add(index);
                paintValues.Add(paints[index]);
            }
        }

        ZPackage package = new();
        package.Write(3);
        package.Write(width);
        package.Write(worldHeights.Length);
        package.Write(heightIndices.Count);
        for (int i = 0; i < heightIndices.Count; i++)
        {
            package.Write(heightIndices[i]);
            package.Write(heightValues[i]);
        }

        package.Write(paintIndices.Count);
        for (int i = 0; i < paintIndices.Count; i++)
        {
            package.Write(paintIndices[i]);
            WriteColor(package, paintValues[i]);
        }

        return Utils.Compress(package.GetArray());
    }

    private static bool ShouldSerializePaints(Heightmap heightmap, int width, Color[] paints)
    {
        if (paints.Length != width * width)
        {
            return false;
        }

        Color[]? basePaints = TryGetBasePaints(heightmap, width);
        if (basePaints == null || basePaints.Length != paints.Length)
        {
            return true;
        }

        for (int i = 0; i < paints.Length; i++)
        {
            if (!Approximately(paints[i], basePaints[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryDeserializeBaseLayer(byte[] payload, Heightmap heightmap, out int width, out float[] worldHeights, out Color[] paints)
    {
        width = 0;
        worldHeights = [];
        paints = [];

        try
        {
            ZPackage package = new(Utils.Decompress(payload));
            int version = package.ReadInt();
            if (version != 3)
            {
                return false;
            }

            width = package.ReadInt();
            int heightCount = package.ReadInt();
            return TryDeserializeSparseBaseLayer(package, heightmap, width, heightCount, out worldHeights, out paints);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeSparseBaseLayer(ZPackage package, Heightmap heightmap, int width, int heightCount, out float[] worldHeights, out Color[] paints)
    {
        worldHeights = new float[heightCount];
        paints = [];
        if (heightmap == null || width <= 0 || width != heightmap.m_width + 1 || heightCount != width * width)
        {
            return false;
        }

        for (int index = 0; index < worldHeights.Length; index++)
        {
            IndexToXZ(index, width, out int x, out int z);
            worldHeights[index] = GetTerrainBaseOrCurrentHeight(heightmap, x, z);
        }

        int heightChanges = package.ReadInt();
        for (int i = 0; i < heightChanges; i++)
        {
            int index = package.ReadInt();
            float value = package.ReadSingle();
            if (index < 0 || index >= worldHeights.Length)
            {
                return false;
            }

            worldHeights[index] = value;
        }

        int paintChanges = package.ReadInt();
        if (paintChanges <= 0)
        {
            return true;
        }

        paints = TryGetBasePaints(heightmap, width) ?? BuildCurrentPaintLayer(heightmap, width);
        if (paints.Length != worldHeights.Length)
        {
            return false;
        }

        for (int i = 0; i < paintChanges; i++)
        {
            int index = package.ReadInt();
            Color value = ReadColor(package);
            if (index < 0 || index >= paints.Length)
            {
                return false;
            }

            paints[index] = value;
        }

        return true;
    }

    private static Color[] BuildCurrentPaintLayer(Heightmap heightmap, int width)
    {
        Color[] paints = new Color[width * width];
        for (int z = 0; z < width; z++)
        {
            for (int x = 0; x < width; x++)
            {
                paints[z * width + x] = GetPaint(heightmap, x, z);
            }
        }

        return paints;
    }

    private static void PersistSupportFillTerrain(
        TerrainComp compiler,
        int width,
        float[] worldHeights,
        Color[] paints,
        TerrainSupportApplyOptions applyOptions)
    {
        if (applyOptions.UseVanillaTerrainDelta)
        {
            PersistSupportFillVanillaDelta(compiler, width, worldHeights, applyOptions.MaxTerrainDelta);
            return;
        }

        PersistSupportFillBaseLayer(compiler, width, worldHeights, paints);
    }

    private static void PersistSupportFillVanillaDelta(TerrainComp compiler, int width, float[] worldHeights, float maxTerrainDelta)
    {
        if (!IsCompilerReady(compiler))
        {
            throw new InvalidOperationException("Target terrain compiler is not network ready.");
        }

        int count = width * width;
        if (worldHeights.Length != count ||
            compiler.m_modifiedHeight.Length != count ||
            compiler.m_levelDelta.Length != count ||
            compiler.m_smoothDelta.Length != count)
        {
            throw new InvalidOperationException("Target terrain compiler size does not match the heightmap.");
        }

        if (!compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        float deltaLimit = maxTerrainDelta > 0f ? maxTerrainDelta : 8f;
        for (int index = 0; index < count; index++)
        {
            IndexToXZ(index, width, out int x, out int z);
            float nativeHeight = GetTerrainBaseOrCurrentHeight(compiler.m_hmap, x, z);
            float delta = Mathf.Clamp(worldHeights[index] - nativeHeight, -deltaLimit, deltaLimit);
            compiler.m_smoothDelta[index] = 0f;
            compiler.m_levelDelta[index] = delta;
            compiler.m_modifiedHeight[index] = Mathf.Abs(delta) > 0.01f;
        }

        compiler.m_nview.GetZDO().Set(SupportFillBaseLayerHash, Array.Empty<byte>());
        PersistCompiler(compiler);
    }

    private static void PersistSupportFillBaseLayer(TerrainComp compiler, int width, float[] worldHeights, Color[] paints)
    {
        if (!IsCompilerReady(compiler))
        {
            throw new InvalidOperationException("Target terrain compiler is not network ready.");
        }

        if (!compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        Array.Clear(compiler.m_modifiedHeight, 0, compiler.m_modifiedHeight.Length);
        Array.Clear(compiler.m_levelDelta, 0, compiler.m_levelDelta.Length);
        Array.Clear(compiler.m_smoothDelta, 0, compiler.m_smoothDelta.Length);
        Array.Clear(compiler.m_modifiedPaint, 0, compiler.m_modifiedPaint.Length);
        Array.Clear(compiler.m_paintMask, 0, compiler.m_paintMask.Length);

        compiler.m_nview.GetZDO().Set(SupportFillBaseLayerHash, SerializeBaseLayer(compiler.m_hmap, width, worldHeights, paints));
        PersistCompiler(compiler);
    }

    private static void PersistCompiler(TerrainComp compiler)
    {
        if (compiler.m_nview != null && compiler.m_nview.IsValid() && !compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        compiler.m_operations++;
        compiler.m_lastOpPoint = Vector3.zero;
        compiler.m_lastOpRadius = 0f;
        compiler.Save();
        compiler.m_hmap.Poke(delayed: false);
    }

    private static void WriteColor(ZPackage package, Color color)
    {
        package.Write(color.r);
        package.Write(color.g);
        package.Write(color.b);
        package.Write(color.a);
    }

    private static Color ReadColor(ZPackage package)
    {
        return new Color(
            package.ReadSingle(),
            package.ReadSingle(),
            package.ReadSingle(),
            package.ReadSingle());
    }
}

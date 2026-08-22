using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    private const float MaxPersistentProxyFootprintRadius = MaxSlopeSize * 0.7071068f;
    private const int ReplayTerrainCheckpointMagic = 0x5A535243;
    private const int MaxReplayTerrainNodes = 262144;
    private const int MaxReplayTerrainDecodedBytes = 16 * 1024 * 1024;

    private static bool TryPrepareReplayCommit(
        ZoneSaviorTerrainProxy proxy,
        ZDO source,
        long batchId,
        int batchCount,
        int batchIndex,
        IReadOnlyList<ReplayExpectedRevision> expectedTerrain,
        out ReplayCommitProposal proposal,
        out string failureReason)
    {
        proposal = null!;
        failureReason = string.Empty;
        if (!proxy || source == null || !source.IsValid())
        {
            failureReason = "the source proxy became unavailable while preparing its terrain commit";
            return false;
        }

        // The transform can briefly lag its ZDO while an object is being streamed. Treat that as
        // retryable; permanently invalidating the batch here would strand the server lease.
        if (!IsProxyTransformSynchronized(source, proxy.transform))
        {
            return false;
        }

        TerrainProxySettings settings = ReadSettings(source);
        Vector3 position = source.GetPosition();
        Quaternion rotation = source.GetRotation();
        if (expectedTerrain.Count == 0 || expectedTerrain.Count > MaxReplayTerrainCompilers)
        {
            failureReason = "the server supplied an invalid terrain compiler manifest";
            return false;
        }

        ZDOMan? world = ZDOMan.instance;
        ZNetScene? scene = ZNetScene.instance;
        if (world == null || scene == null)
        {
            return false;
        }

        List<Heightmap> heightmaps = [];
        Heightmap.FindHeightmap(position, settings.SearchRadius + SearchPadding, heightmaps);

        List<TerrainComp> compilers = [];
        List<Heightmap> preparedHeightmaps = [];
        List<int> widths = [];
        HashSet<TerrainComp> seen = [];
        HashSet<ZDOID> expectedIds = [];
        foreach (ReplayExpectedRevision expected in expectedTerrain)
        {
            if (expected.ZdoId.IsNone() || !expectedIds.Add(expected.ZdoId))
            {
                failureReason = "the server supplied a duplicate terrain compiler manifest entry";
                return false;
            }

            ZDO terrainZdo = world.GetZDO(expected.ZdoId);
            ZNetView view = terrainZdo != null ? scene.FindInstance(terrainZdo) : null!;
            TerrainComp compiler = view ? view.GetComponent<TerrainComp>() : null!;
            if (terrainZdo == null || !view || !compiler)
            {
                return false;
            }

            if (!seen.Add(compiler) ||
                !TryInspectTerrainCompiler(compiler, out Heightmap preparedHeightmap, out int width) ||
                preparedHeightmap.IsDistantLod ||
                !FootprintIntersectsHeightmap(position, rotation, settings, preparedHeightmap))
            {
                failureReason = "the live terrain compiler does not match the server manifest";
                return false;
            }

            ZDO liveTerrainZdo = compiler.m_nview && compiler.m_nview.IsValid()
                ? compiler.m_nview.GetZDO()
                : null!;
            if (!ReferenceEquals(liveTerrainZdo, terrainZdo) ||
                terrainZdo.GetPrefab() != TerrainCompilerPrefabHash)
            {
                failureReason = "the live terrain compiler ZDO does not match the server manifest";
                return false;
            }

            if (terrainZdo.DataRevision != expected.DataRevision ||
                compiler.m_lastDataRevision != terrainZdo.DataRevision ||
                (!ZNet.instance.IsServer() && world.m_clientChangeQueue.Contains(terrainZdo.m_uid)))
            {
                return false;
            }

            compilers.Add(compiler);
            preparedHeightmaps.Add(preparedHeightmap);
            widths.Add(width);
        }

        if (compilers.Count != expectedTerrain.Count ||
            !IsFootprintCoveredByHeightmaps(
                position,
                rotation,
                settings,
                heightmaps,
                preparedHeightmaps))
        {
            return false;
        }

        if (settings.ModifiesHeight)
        {
            ZoneSaviorTerrainMistileCompat.RegisterIgnoredTerrainArea(
                position,
                settings.SearchRadius,
                "ZoneSavior terrain proxy");
        }

        List<ReplayTerrainCommitEntry> terrain = new(compilers.Count);
        for (int index = 0; index < compilers.Count; index++)
        {
            TerrainComp compiler = compilers[index];
            ZDO terrainZdo = compiler.m_nview.GetZDO();

            uint baseRevision = terrainZdo.DataRevision;
            byte[]? existingData = terrainZdo.GetByteArray(ZDOVars.s_TCData);
            byte[] committedData;
            if (TryReadReplayTerrainCheckpoint(
                    existingData,
                    out long checkpointBatch,
                    out int checkpointIndex) &&
                checkpointBatch == batchId &&
                checkpointIndex == batchIndex)
            {
                committedData = existingData!;
            }
            else
            {
                TerrainCompilerSnapshot snapshot = new(compiler);
                try
                {
                    bool changed = ModifyTerrainCompiler(
                        compiler,
                        preparedHeightmaps[index],
                        widths[index],
                        position,
                        rotation,
                        settings);
                    if (changed)
                    {
                        compiler.m_operations++;
                        compiler.m_lastOpPoint = position;
                        compiler.m_lastOpRadius = settings.SearchRadius;
                    }

                    if (!TrySerializeReplayTerrainCompiler(
                            compiler,
                            existingData,
                            batchId,
                            batchIndex,
                            out committedData))
                    {
                        failureReason = "the canonical terrain compiler payload could not be preserved safely";
                        return false;
                    }
                }
                finally
                {
                    snapshot.Restore(compiler);
                }
            }

            if (terrainZdo.DataRevision != baseRevision)
            {
                return false;
            }

            if (committedData.Length == 0 || committedData.Length > MaxReplayTerrainPayloadBytes)
            {
                failureReason = "a serialized terrain compiler exceeded the safe payload limit";
                return false;
            }

            terrain.Add(new ReplayTerrainCommitEntry(terrainZdo.m_uid, baseRevision, committedData));
        }

        proposal = new ReplayCommitProposal(
            batchId,
            batchCount,
            batchIndex,
            source.m_uid,
            source.DataRevision,
            terrain);
        return true;
    }

    private static bool IsReplayTerrainFootprintLoaded(ZoneSaviorTerrainProxy proxy, ZDO source)
    {
        if (!proxy || source == null || !source.IsValid() || !IsProxyTransformSynchronized(source, proxy.transform))
        {
            return false;
        }

        TerrainProxySettings settings = ReadSettings(source);
        Vector3 position = proxy.transform.position;
        Quaternion rotation = proxy.transform.rotation;
        List<Heightmap> loadedHeightmaps = [];
        Heightmap.FindHeightmap(position, settings.SearchRadius + SearchPadding, loadedHeightmaps);
        List<Heightmap> usableHeightmaps = loadedHeightmaps
            .Where(heightmap =>
                heightmap &&
                !heightmap.IsDistantLod &&
                FootprintIntersectsHeightmap(position, rotation, settings, heightmap))
            .ToList();
        ZNetScene? scene = ZNetScene.instance;
        return scene != null &&
               usableHeightmaps.All(heightmap => scene.IsAreaReady(heightmap.transform.position)) &&
               IsFootprintCoveredByHeightmaps(
            position,
            rotation,
            settings,
            loadedHeightmaps,
            usableHeightmaps);
    }

    private static bool IsProxyTransformSynchronized(ZDO zdo, Transform transform)
    {
        Vector3 storedPosition = zdo.GetPosition();
        Quaternion storedRotation = zdo.GetRotation();
        return IsFinite(storedPosition) &&
               Utils.DistanceXZ(storedPosition, transform.position) < 0.1f &&
               Mathf.Abs(storedPosition.y - transform.position.y) < 0.1f &&
               IsFinite(storedRotation) &&
               Quaternion.Angle(storedRotation, transform.rotation) < 0.1f;
    }

    private static bool TrySerializeReplayTerrainCompiler(
        TerrainComp compiler,
        byte[]? existingData,
        long batchId,
        int batchIndex,
        out byte[] data)
    {
        data = [];
        byte[] trailingData = [];
        if (existingData is { Length: > 0 })
        {
            if (!TryParseReplayTerrainData(existingData, out ReplayTerrainData existing) ||
                existing.HeightCount != compiler.m_modifiedHeight.Length ||
                existing.PaintCount != compiler.m_modifiedPaint.Length)
            {
                return false;
            }

            trailingData = existing.TrailingData;
        }

        ZPackage package = new();
        package.Write(1);
        package.Write(compiler.m_operations);
        package.Write(compiler.m_lastOpPoint);
        package.Write(compiler.m_lastOpRadius);
        package.Write(compiler.m_modifiedHeight.Length);
        for (int index = 0; index < compiler.m_modifiedHeight.Length; index++)
        {
            package.Write(compiler.m_modifiedHeight[index]);
            if (compiler.m_modifiedHeight[index])
            {
                package.Write(compiler.m_levelDelta[index]);
                package.Write(compiler.m_smoothDelta[index]);
            }
        }

        package.Write(compiler.m_modifiedPaint.Length);
        for (int index = 0; index < compiler.m_modifiedPaint.Length; index++)
        {
            package.Write(compiler.m_modifiedPaint[index]);
            if (compiler.m_modifiedPaint[index])
            {
                Color paint = compiler.m_paintMask[index];
                package.Write(paint.r);
                package.Write(paint.g);
                package.Write(paint.b);
                package.Write(paint.a);
            }
        }

        // TerrainComp.Load ignores trailing bytes. Preserve bytes owned by other mods and replace
        // only ZoneSavior's fixed-size checkpoint at the end of the payload.
        foreach (byte value in trailingData)
        {
            package.Write(value);
        }

        package.Write(ReplayTerrainCheckpointMagic);
        package.Write(batchId);
        package.Write(batchIndex);
        data = Utils.Compress(package.GetArray());
        return data.Length > 0 && data.Length <= MaxReplayTerrainPayloadBytes;
    }

    private static bool TryReadReplayTerrainCheckpoint(
        byte[]? data,
        out long batchId,
        out int batchIndex)
    {
        batchId = 0L;
        batchIndex = -1;
        return TryParseReplayTerrainData(data, out ReplayTerrainData terrain) &&
               terrain.HasCheckpoint &&
               (batchId = terrain.CheckpointBatch) > 0L &&
               (batchIndex = terrain.CheckpointIndex) >= 0;
    }

    private static bool TryParseReplayTerrainData(
        byte[]? data,
        out ReplayTerrainData terrain)
    {
        terrain = null!;
        if (data == null || data.Length == 0 || data.Length > MaxReplayTerrainPayloadBytes)
        {
            return false;
        }

        try
        {
            if (!TryDecompressReplayTerrainPayload(data, out byte[] decoded))
            {
                return false;
            }

            ZPackage package = new(decoded);
            if (package.ReadInt() != 1)
            {
                return false;
            }

            int operations = package.ReadInt();
            Vector3 lastPoint = package.ReadVector3();
            float lastRadius = package.ReadSingle();
            if (operations < 0 || !IsFinite(lastPoint) || !IsFinite(lastRadius))
            {
                return false;
            }

            int heightCount = package.ReadInt();
            if (heightCount <= 0 || heightCount > MaxReplayTerrainNodes)
            {
                return false;
            }

            int width = Mathf.RoundToInt(Mathf.Sqrt(heightCount)) - 1;
            if (width <= 0 || (width + 1) * (width + 1) != heightCount)
            {
                return false;
            }

            bool[] modifiedHeight = new bool[heightCount];
            float[] levelDelta = new float[heightCount];
            float[] smoothDelta = new float[heightCount];
            for (int index = 0; index < heightCount; index++)
            {
                bool modified = package.ReadBool();
                modifiedHeight[index] = modified;
                if (modified)
                {
                    float level = package.ReadSingle();
                    float smooth = package.ReadSingle();
                    if (!IsFinite(level) || !IsFinite(smooth))
                    {
                        return false;
                    }

                    levelDelta[index] = level;
                    smoothDelta[index] = smooth;
                }
            }

            int paintCount = package.ReadInt();
            int legacyPaintCount = width * width;
            if (paintCount != heightCount && paintCount != legacyPaintCount)
            {
                return false;
            }

            bool[] rawModifiedPaint = new bool[paintCount];
            Color[] rawPaintMask = new Color[paintCount];
            for (int index = 0; index < paintCount; index++)
            {
                bool modified = package.ReadBool();
                rawModifiedPaint[index] = modified;
                if (!modified)
                {
                    continue;
                }

                Color paint = new(
                    package.ReadSingle(),
                    package.ReadSingle(),
                    package.ReadSingle(),
                    package.ReadSingle());
                if (!IsFinite(paint.r) ||
                    !IsFinite(paint.g) ||
                    !IsFinite(paint.b) ||
                    !IsFinite(paint.a))
                {
                    return false;
                }

                rawPaintMask[index] = paint;
            }

            bool[] modifiedPaint = rawModifiedPaint;
            Color[] paintMask = rawPaintMask;
            if (paintCount == legacyPaintCount)
            {
                modifiedPaint = new bool[heightCount];
                paintMask = new Color[heightCount];
                int nodeWidth = width + 1;
                for (int z = 0; z < nodeWidth; z++)
                {
                    for (int x = 0; x < nodeWidth; x++)
                    {
                        int sourceX = Mathf.Min(x, width - 1);
                        int sourceZ = Mathf.Min(z, width - 1);
                        int sourceIndex = sourceZ * width + sourceX;
                        int targetIndex = z * nodeWidth + x;
                        modifiedPaint[targetIndex] = rawModifiedPaint[sourceIndex];
                        paintMask[targetIndex] = rawPaintMask[sourceIndex];
                    }
                }
            }

            int remaining = package.Size() - package.GetPos();
            if (remaining < 0)
            {
                return false;
            }

            byte[] trailing = remaining == 0 ? [] : package.ReadByteArray(remaining);
            bool hasCheckpoint = TrySplitReplayTerrainCheckpoint(
                trailing,
                out byte[] preservedTrailing,
                out long checkpointBatch,
                out int checkpointIndex);
            terrain = new ReplayTerrainData(
                operations,
                lastPoint,
                lastRadius,
                modifiedHeight,
                levelDelta,
                smoothDelta,
                modifiedPaint,
                paintMask,
                preservedTrailing,
                hasCheckpoint,
                checkpointBatch,
                checkpointIndex);
            return package.GetPos() == package.Size();
        }
        catch
        {
            terrain = null!;
            return false;
        }
    }

    private static bool TrySplitReplayTerrainCheckpoint(
        byte[] trailing,
        out byte[] preservedTrailing,
        out long batchId,
        out int batchIndex)
    {
        const int checkpointSize = sizeof(int) + sizeof(long) + sizeof(int);
        preservedTrailing = trailing;
        batchId = 0L;
        batchIndex = -1;
        if (trailing.Length < checkpointSize)
        {
            return false;
        }

        try
        {
            byte[] checkpoint = new byte[checkpointSize];
            Buffer.BlockCopy(trailing, trailing.Length - checkpointSize, checkpoint, 0, checkpointSize);
            ZPackage package = new(checkpoint);
            if (package.ReadInt() != ReplayTerrainCheckpointMagic)
            {
                return false;
            }

            batchId = package.ReadLong();
            batchIndex = package.ReadInt();
            if (batchId <= 0L || batchIndex < 0 || package.GetPos() != package.Size())
            {
                batchId = 0L;
                batchIndex = -1;
                return false;
            }

            int preservedLength = trailing.Length - checkpointSize;
            preservedTrailing = new byte[preservedLength];
            if (preservedLength > 0)
            {
                Buffer.BlockCopy(trailing, 0, preservedTrailing, 0, preservedLength);
            }

            return true;
        }
        catch
        {
            preservedTrailing = trailing;
            batchId = 0L;
            batchIndex = -1;
            return false;
        }
    }

    private static bool TryValidateReplayTerrainTransition(
        ZDO source,
        ZDO terrainZdo,
        byte[] proposedData,
        long expectedBatch,
        int expectedIndex,
        int expectedTerrainNodes,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (!TryParseReplayTerrainData(proposedData, out ReplayTerrainData proposed) ||
            !proposed.HasCheckpoint ||
            proposed.CheckpointBatch != expectedBatch ||
            proposed.CheckpointIndex != expectedIndex ||
            proposed.HeightCount != expectedTerrainNodes ||
            proposed.PaintCount != expectedTerrainNodes)
        {
            failureReason = "the proposed terrain payload has invalid dimensions or checkpoint metadata";
            return false;
        }

        byte[]? canonicalBytes = terrainZdo.GetByteArray(ZDOVars.s_TCData);
        ReplayTerrainData canonical;
        if (canonicalBytes is { Length: > 0 })
        {
            if (!TryParseReplayTerrainData(canonicalBytes, out canonical) ||
                canonical.HeightCount != expectedTerrainNodes ||
                canonical.PaintCount != expectedTerrainNodes)
            {
                failureReason = "the canonical terrain payload could not be parsed safely";
                return false;
            }
        }
        else
        {
            canonical = ReplayTerrainData.CreateEmpty(expectedTerrainNodes);
        }

        if (!canonical.TrailingData.SequenceEqual(proposed.TrailingData))
        {
            failureReason = "the proposed terrain payload changed opaque trailing data";
            return false;
        }

        if (canonical.HasCheckpoint && canonical.CheckpointBatch == expectedBatch)
        {
            if (canonical.CheckpointIndex > expectedIndex)
            {
                failureReason = "the canonical terrain checkpoint is ahead of the active replay operation";
                return false;
            }

            if (canonical.CheckpointIndex == expectedIndex)
            {
                if (!ReplayTerrainCoreEquals(canonical, proposed))
                {
                    failureReason =
                        "the proposed terrain payload does not match the already committed replay checkpoint";
                    return false;
                }

                return true;
            }
        }

        if (!TryBuildExpectedReplayTerrainTransition(
                canonical,
                source,
                terrainZdo,
                expectedTerrainNodes,
                expectedBatch,
                expectedIndex,
                out ReplayTerrainData expected,
                out failureReason))
        {
            return false;
        }

        if (!ReplayTerrainCoreEquals(expected, proposed))
        {
            failureReason =
                "the proposed terrain payload does not match the server-recomputed proxy transition";
            return false;
        }

        return true;
    }

    private static bool IsValidReplaySettings(TerrainProxySettings settings)
    {
        if (!IsFinite(settings.Radius) ||
            !IsFinite(settings.Width) ||
            !IsFinite(settings.Length) ||
            !IsFinite(settings.SlopeHeightDelta) ||
            !IsFinite(settings.TerrainEdgeSoftness))
        {
            return false;
        }

        return settings.Mode switch
        {
            TerrainProxyMode.Circle => true,
            TerrainProxyMode.Slope => true,
            TerrainProxyMode.Paint => settings.PaintType is
                AdminTerrainPaintType.Grass or
                AdminTerrainPaintType.Dirt or
                AdminTerrainPaintType.Cultivated or
                AdminTerrainPaintType.Paved or
                AdminTerrainPaintType.DarkGrass or
                AdminTerrainPaintType.PatchyGrass or
                AdminTerrainPaintType.MossyPaving or
                AdminTerrainPaintType.DirtPaving or
                AdminTerrainPaintType.DarkPaving or
                AdminTerrainPaintType.ClearVegetation,
            _ => false
        };
    }

    private static bool TryBuildExpectedReplayTerrainTransition(
        ReplayTerrainData canonical,
        ZDO source,
        ZDO terrainZdo,
        int expectedTerrainNodes,
        long expectedBatch,
        int expectedIndex,
        out ReplayTerrainData expected,
        out string failureReason)
    {
        expected = null!;
        failureReason = string.Empty;
        TerrainProxySettings settings = ReadSettings(source);
        Vector3 sourcePosition = source.GetPosition();
        Quaternion sourceRotation = source.GetRotation();
        Vector3 terrainPosition = terrainZdo.GetPosition();
        if (!IsValidReplaySettings(settings) ||
            !IsFinite(sourcePosition) ||
            !IsFinite(sourceRotation) ||
            !IsFinite(terrainPosition) ||
            !TryGetReplayTerrainLayout(
                expectedTerrainNodes,
                out Heightmap template,
                out int nodeWidth,
                out float nodeScale) ||
            HeightmapBuilder.instance == null ||
            WorldGenerator.instance == null)
        {
            failureReason = "the source proxy or procedural terrain layout is invalid";
            return false;
        }

        try
        {
            HeightmapBuilder.HMBuildData buildData = HeightmapBuilder.instance.RequestTerrainSync(
                terrainPosition,
                nodeWidth - 1,
                nodeScale,
                template.IsDistantLod,
                WorldGenerator.instance);
            if (buildData?.m_baseHeights == null ||
                buildData.m_baseHeights.Count != expectedTerrainNodes ||
                buildData.m_baseMask == null ||
                buildData.m_baseMask.Length != expectedTerrainNodes)
            {
                failureReason = "the procedural base terrain is not ready";
                return false;
            }

            bool[] modifiedHeight = (bool[])canonical.ModifiedHeight.Clone();
            float[] levelDelta = (float[])canonical.LevelDelta.Clone();
            float[] smoothDelta = (float[])canonical.SmoothDelta.Clone();
            bool[] modifiedPaint = (bool[])canonical.ModifiedPaint.Clone();
            Color[] paintMask = (Color[])canonical.PaintMask.Clone();
            Quaternion inverseYaw = InverseYaw(sourceRotation);
            int terrainWidth = nodeWidth - 1;
            bool changed = false;
            for (int index = 0; index < expectedTerrainNodes; index++)
            {
                float baseHeight = buildData.m_baseHeights[index];
                Color basePaint = buildData.m_baseMask[index];
                if (!IsFinite(baseHeight) ||
                    !IsFinite(basePaint.r) ||
                    !IsFinite(basePaint.g) ||
                    !IsFinite(basePaint.b) ||
                    !IsFinite(basePaint.a))
                {
                    failureReason = "the procedural base terrain contains a non-finite value";
                    return false;
                }

                int x = index % nodeWidth;
                int z = index / nodeWidth;
                Vector3 node = terrainPosition;
                node.x += (x - terrainWidth * 0.5f) * nodeScale;
                node.z += (z - terrainWidth * 0.5f) * nodeScale;
                if (!TryGetAffectedTerrainNode(
                        sourcePosition,
                        inverseYaw,
                        settings,
                        node,
                        out Vector3 local,
                        out float normalized))
                {
                    continue;
                }

                if (settings.ModifiesHeight)
                {
                    float currentHeight = Mathf.Clamp(
                        baseHeight + canonical.LevelDelta[index] + canonical.SmoothDelta[index],
                        baseHeight - AdminTerrainMaxDelta,
                        baseHeight + AdminTerrainMaxDelta);
                    float targetHeight = settings.GetTargetHeight(
                        sourcePosition,
                        terrainPosition.y,
                        local.z);
                    float delta =
                        (targetHeight - currentHeight + smoothDelta[index]) *
                        settings.GetLevelFalloff(local.x, local.z);
                    smoothDelta[index] = 0f;
                    float previous = levelDelta[index];
                    levelDelta[index] = Mathf.Clamp(
                        previous + delta,
                        -AdminTerrainMaxDelta,
                        AdminTerrainMaxDelta);
                    modifiedHeight[index] = Mathf.Abs(levelDelta[index]) > 0.001f;
                    changed |= Mathf.Abs(previous - levelDelta[index]) > 0.001f;
                }

                if (settings.ModifiesPaint && HasPaintInfluence(normalized))
                {
                    Color current = QuantizeReplayPaint(
                        canonical.ModifiedPaint[index]
                            ? canonical.PaintMask[index]
                            : basePaint);
                    Color target = GetPaintTarget(settings.PaintType, current);
                    Color desired = Color.Lerp(
                        current,
                        target,
                        Mathf.Pow(1f - Mathf.Clamp01(normalized), 0.1f));
                    if (settings.PaintType != AdminTerrainPaintType.ClearVegetation)
                    {
                        desired.a = current.a;
                    }

                    if (modifiedPaint[index])
                    {
                        if (!Approximately(paintMask[index], desired))
                        {
                            paintMask[index] = desired;
                            changed = true;
                        }
                    }
                    else if (!Approximately(current, desired))
                    {
                        modifiedPaint[index] = true;
                        paintMask[index] = desired;
                        changed = true;
                    }
                }
            }

            int operations = canonical.Operations;
            Vector3 lastOpPoint = canonical.LastOpPoint;
            float lastOpRadius = canonical.LastOpRadius;
            if (changed)
            {
                if (operations == int.MaxValue)
                {
                    failureReason = "the terrain operation counter is exhausted";
                    return false;
                }

                operations++;
                lastOpPoint = sourcePosition;
                lastOpRadius = settings.SearchRadius;
            }

            expected = new ReplayTerrainData(
                operations,
                lastOpPoint,
                lastOpRadius,
                modifiedHeight,
                levelDelta,
                smoothDelta,
                modifiedPaint,
                paintMask,
                canonical.TrailingData,
                true,
                expectedBatch,
                expectedIndex);
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"the procedural terrain transition could not be recomputed: {ex.Message}";
            return false;
        }
    }

    private static bool TryGetReplayTerrainLayout(
        int expectedTerrainNodes,
        out Heightmap template,
        out int nodeWidth,
        out float nodeScale)
    {
        nodeWidth = Mathf.RoundToInt(Mathf.Sqrt(expectedTerrainNodes));
        nodeScale = 0f;
        template = ZoneSystem.instance && ZoneSystem.instance.m_zonePrefab
            ? ZoneSystem.instance.m_zonePrefab.GetComponentInChildren<Heightmap>()
            : null!;
        if (!template ||
            nodeWidth <= 1 ||
            nodeWidth * nodeWidth != expectedTerrainNodes ||
            template.m_width + 1 != nodeWidth ||
            !IsFinite(template.m_scale) ||
            template.m_scale <= 0f)
        {
            return false;
        }

        nodeScale = template.m_scale;
        return true;
    }

    private static bool ReplayTerrainCoreEquals(
        ReplayTerrainData left,
        ReplayTerrainData right)
    {
        if (left.Operations != right.Operations ||
            !ReplayVectorEquals(left.LastOpPoint, right.LastOpPoint) ||
            left.LastOpRadius != right.LastOpRadius ||
            left.HeightCount != right.HeightCount ||
            left.PaintCount != right.PaintCount)
        {
            return false;
        }

        for (int index = 0; index < left.HeightCount; index++)
        {
            if (!ReplayHeightNodeEquals(left, right, index) ||
                !ReplayPaintNodeEquals(left, right, index))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReplayHeightNodeEquals(
        ReplayTerrainData left,
        ReplayTerrainData right,
        int index)
    {
        return left.ModifiedHeight[index] == right.ModifiedHeight[index] &&
               left.LevelDelta[index] == right.LevelDelta[index] &&
               left.SmoothDelta[index] == right.SmoothDelta[index];
    }

    private static bool ReplayPaintNodeEquals(
        ReplayTerrainData left,
        ReplayTerrainData right,
        int index)
    {
        return left.ModifiedPaint[index] == right.ModifiedPaint[index] &&
               ReplayColorEquals(left.PaintMask[index], right.PaintMask[index]);
    }

    private static bool ReplayVectorEquals(Vector3 left, Vector3 right)
    {
        return left.x == right.x && left.y == right.y && left.z == right.z;
    }

    private static bool ReplayColorEquals(Color left, Color right)
    {
        return left.r == right.r &&
               left.g == right.g &&
               left.b == right.b &&
               left.a == right.a;
    }

    private static Color QuantizeReplayPaint(Color value)
    {
        return (Color)(Color32)value;
    }

    private static bool TryDecompressReplayTerrainPayload(byte[] data, out byte[] decoded)
    {
        decoded = [];
        try
        {
            using MemoryStream input = new(data, writable: false);
            using GZipStream gzip = new(input, CompressionMode.Decompress);
            using MemoryStream output = new(Math.Min(data.Length * 4, MaxReplayTerrainDecodedBytes));
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = gzip.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaxReplayTerrainDecodedBytes)
                {
                    return false;
                }

                output.Write(buffer, 0, read);
            }

            decoded = output.ToArray();
            return decoded.Length > 0;
        }
        catch
        {
            decoded = [];
            return false;
        }
    }

    private static bool IsFootprintCoveredByHeightmaps(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        List<Heightmap> loadedHeightmaps,
        List<Heightmap> preparedHeightmaps,
        bool includeCircleBoundary = false)
    {
        Heightmap anchor = null!;
        foreach (Heightmap heightmap in loadedHeightmaps)
        {
            if (heightmap && !heightmap.IsDistantLod && heightmap.IsPointInside(position))
            {
                anchor = heightmap;
                break;
            }
        }

        if (!anchor || !preparedHeightmaps.Contains(anchor))
        {
            return false;
        }

        float tileSize = anchor.m_width * anchor.m_scale;
        if (tileSize <= 0f || float.IsNaN(tileSize) || float.IsInfinity(tileSize))
        {
            return false;
        }

        float halfTile = tileSize * 0.5f;
        float radius = settings.SearchRadius;
        Vector3 anchorPosition = anchor.transform.position;
        int minTileX = Mathf.CeilToInt((position.x - radius - halfTile - anchorPosition.x) / tileSize - 0.001f);
        int maxTileX = Mathf.FloorToInt((position.x + radius + halfTile - anchorPosition.x) / tileSize + 0.001f);
        int minTileZ = Mathf.CeilToInt((position.z - radius - halfTile - anchorPosition.z) / tileSize - 0.001f);
        int maxTileZ = Mathf.FloorToInt((position.z + radius + halfTile - anchorPosition.z) / tileSize + 0.001f);
        float centerTolerance = Mathf.Max(anchor.m_scale * 0.01f, 0.001f);
        bool intersectsAnyTile = false;

        for (int tileZ = minTileZ; tileZ <= maxTileZ; tileZ++)
        {
            for (int tileX = minTileX; tileX <= maxTileX; tileX++)
            {
                Vector3 tileCenter = new(
                    anchorPosition.x + tileX * tileSize,
                    anchorPosition.y,
                    anchorPosition.z + tileZ * tileSize);
                if (!FootprintIntersectsTile(
                        position,
                        rotation,
                        settings,
                        tileCenter,
                        tileSize,
                        includeCircleBoundary))
                {
                    continue;
                }

                intersectsAnyTile = true;
                bool covered = false;
                foreach (Heightmap preparedHeightmap in preparedHeightmaps)
                {
                    if (!preparedHeightmap)
                    {
                        continue;
                    }

                    float preparedTileSize = preparedHeightmap.m_width * preparedHeightmap.m_scale;
                    Vector3 preparedCenter = preparedHeightmap.transform.position;
                    if (Mathf.Abs(preparedTileSize - tileSize) <= centerTolerance &&
                        Mathf.Abs(preparedCenter.x - tileCenter.x) <= centerTolerance &&
                        Mathf.Abs(preparedCenter.z - tileCenter.z) <= centerTolerance)
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                {
                    return false;
                }
            }
        }

        return intersectsAnyTile;
    }

    private static bool FootprintIntersectsHeightmap(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        Heightmap heightmap,
        bool includeCircleBoundary = false)
    {
        float tileSize = heightmap.m_width * heightmap.m_scale;
        return tileSize > 0f &&
               FootprintIntersectsTile(
                   position,
                   rotation,
                   settings,
                   heightmap.transform.position,
                   tileSize,
                   includeCircleBoundary);
    }

    private static bool FootprintIntersectsTile(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        Vector3 tileCenter,
        float tileSize,
        bool includeCircleBoundary = false)
    {
        if (settings.HasCircleFootprint)
        {
            bool includeBoundary = includeCircleBoundary ||
                                   (settings.Mode != TerrainProxyMode.Paint &&
                                    settings.TerrainEdgeSoftness <= 0f);
            return CircleIntersectsRect(
                position,
                settings.Radius,
                tileCenter,
                Quaternion.identity,
                tileSize,
                tileSize,
                includeBoundary);
        }

        return RectIntersectsRect(
            position,
            rotation,
            settings.Width,
            settings.Length,
            tileCenter,
            Quaternion.identity,
            tileSize,
            tileSize);
    }

    private static bool TryResetTerrainAndIntersectingProxyObjects(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        TerrainResetScope resetScope,
        ZNetView? placedView,
        out int terrainCompilers,
        out int removed,
        out string failureReason)
    {
        terrainCompilers = 0;
        removed = 0;
        failureReason = string.Empty;
        List<PreparedTerrainReset> preparedResets = [];
        if (!TryCollectIntersectingPersistentProxyViews(
                position,
                rotation,
                settings,
                resetScope,
                placedView,
                out List<LoadedProxyResetCandidate> proxyCandidates,
                out failureReason))
        {
            return false;
        }

        TerrainResetChannels resetChannels = resetScope == TerrainResetScope.PaintOnly
            ? TerrainResetChannels.Paint
            : TerrainResetChannels.Height | TerrainResetChannels.Paint;
        if (!TryPrepareTerrainReset(
                position,
                rotation,
                settings,
                resetChannels,
                out PreparedTerrainReset preparedReset,
                out failureReason))
        {
            return false;
        }

        preparedResets.Add(preparedReset);
        foreach (LoadedProxyResetCandidate candidate in proxyCandidates)
        {
            if (!TryPrepareTerrainReset(
                    candidate.Position,
                    candidate.Rotation,
                    candidate.Settings,
                    candidate.Channels,
                    out PreparedTerrainReset preparedProxyReset,
                    out failureReason))
            {
                return false;
            }

            preparedResets.Add(preparedProxyReset);
        }

        HashSet<TerrainComp> claimedCompilers = [];
        foreach (PreparedTerrainReset reset in preparedResets)
        {
            foreach (PreparedTerrainResetCompiler preparedCompiler in reset.Compilers)
            {
                if (claimedCompilers.Add(preparedCompiler.Compiler) &&
                    !TryClaimTerrainCompilerOwnership(preparedCompiler.Compiler))
                {
                    failureReason = "a required terrain compiler could not be claimed";
                    return false;
                }
            }
        }

        foreach (LoadedProxyResetCandidate candidate in proxyCandidates)
        {
            ZNetView view = candidate.View;
            if (!view ||
                !view.IsValid() ||
                !ReferenceEquals(view.GetZDO(), candidate.Zdo))
            {
                failureReason = "an intersecting terrain proxy became unavailable";
                return false;
            }

            if (!view.IsOwner())
            {
                view.ClaimOwnership();
            }

            if (!view.IsOwner())
            {
                failureReason = "an intersecting terrain proxy could not be claimed";
                return false;
            }
        }

        HashSet<TerrainComp> changedCompilers = [];
        foreach (PreparedTerrainReset reset in preparedResets)
        {
            bool areaChanged = false;
            foreach (PreparedTerrainResetCompiler preparedCompiler in reset.Compilers)
            {
                if (!ResetTerrainCompiler(
                        preparedCompiler.Compiler,
                        preparedCompiler.Heightmap,
                        preparedCompiler.Width,
                        reset.Position,
                        reset.Rotation,
                        reset.Settings,
                        reset.Channels))
                {
                    continue;
                }

                changedCompilers.Add(preparedCompiler.Compiler);
                areaChanged = true;
            }

            if (areaChanged)
            {
                ClutterSystem.instance?.ResetGrass(
                    reset.Position,
                    reset.Settings.SearchRadius + SearchPadding);
            }
        }

        terrainCompilers = changedCompilers.Count;
        foreach (LoadedProxyResetCandidate candidate in proxyCandidates)
        {
            DestroyProxy(candidate.View);
            removed++;
        }

        return true;
    }

    private static bool TryCollectIntersectingPersistentProxyViews(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        TerrainResetScope resetScope,
        ZNetView? placedView,
        out List<LoadedProxyResetCandidate> candidates,
        out string failureReason)
    {
        candidates = [];
        failureReason = string.Empty;
        ZNetScene scene = ZNetScene.instance;
        ZDOMan zdoMan = ZDOMan.instance;
        if (scene == null || zdoMan == null)
        {
            failureReason = "terrain proxy state is not ready";
            return false;
        }

        float searchRadius = settings.SearchRadius + MaxPersistentProxyFootprintRadius;
        Vector2i minZone = ZoneSystem.GetZone(
            new Vector3(position.x - searchRadius, position.y, position.z - searchRadius));
        Vector2i maxZone = ZoneSystem.GetZone(
            new Vector3(position.x + searchRadius, position.y, position.z + searchRadius));
        List<ZDO> knownZdos = [];
        for (int zoneY = minZone.y; zoneY <= maxZone.y; zoneY++)
        {
            for (int zoneX = minZone.x; zoneX <= maxZone.x; zoneX++)
            {
                zdoMan.FindObjects(new Vector2i(zoneX, zoneY), knownZdos);
            }
        }

        // A remote client retains unloaded known ZDOs in this outside-sector bucket.
        zdoMan.FindObjects(new Vector2i(int.MinValue, int.MinValue), knownZdos);
        // Include live keys as a fail-closed check for inconsistent sector bookkeeping.
        knownZdos.AddRange(scene.m_instances.Keys);
        HashSet<ZDOID> seen = [];
        foreach (ZDO zdo in knownZdos)
        {
            if (zdo == null ||
                !zdo.IsValid() ||
                !IsPersistentProxyPrefabHash(zdo.GetPrefab()) ||
                !zdo.Persistent ||
                !seen.Add(zdo.m_uid))
            {
                continue;
            }

            Vector3 proxyPosition = zdo.GetPosition();
            if (!IsFinite(proxyPosition) ||
                Utils.DistanceXZ(position, proxyPosition) > searchRadius)
            {
                continue;
            }

            Quaternion proxyRotation = zdo.GetRotation();
            if (!TryReadPersistentProxyResetSettings(
                    zdo,
                    proxyRotation,
                    out TerrainProxySettings proxySettings))
            {
                failureReason = "an intersecting terrain proxy has invalid stored settings";
                return false;
            }

            TerrainResetChannels proxyChannels = GetTerrainResetChannels(proxySettings);
            if (proxyChannels == TerrainResetChannels.None ||
                (resetScope == TerrainResetScope.PaintOnly &&
                 (proxyChannels & TerrainResetChannels.Paint) == 0) ||
                !FootprintsIntersect(
                    position,
                    rotation,
                    settings,
                    proxyPosition,
                    proxyRotation,
                    proxySettings))
            {
                continue;
            }

            if (!zdoMan.m_objectsByID.TryGetValue(zdo.m_uid, out ZDO canonicalZdo) ||
                !ReferenceEquals(canonicalZdo, zdo))
            {
                failureReason = "intersecting terrain proxy state is inconsistent";
                return false;
            }

            if (!scene.m_instances.TryGetValue(zdo, out ZNetView view) ||
                !view ||
                view == placedView ||
                !view.IsValid() ||
                !ReferenceEquals(view.GetZDO(), zdo) ||
                !IsProxyObject(view.gameObject))
            {
                failureReason = "an intersecting terrain proxy is not loaded";
                return false;
            }

            candidates.Add(new LoadedProxyResetCandidate(
                zdo,
                view,
                proxyPosition,
                proxyRotation,
                proxySettings,
                proxyChannels));
        }

        return true;
    }

    private static bool IsPersistentProxyPrefabHash(int prefabHash)
    {
        return prefabHash == PrefabHash ||
               prefabHash == SlopePrefabHash ||
               prefabHash == PaintPrefabHash;
    }

    private static bool TryReadPersistentProxyResetSettings(
        ZDO zdo,
        Quaternion rotation,
        out TerrainProxySettings settings)
    {
        settings = default;
        if (!HasStoredSettings(zdo) || !IsFinite(rotation))
        {
            return false;
        }

        settings = ReadSettings(zdo);
        int prefabHash = zdo.GetPrefab();
        bool modeMatchesPrefab =
            (prefabHash == PrefabHash && settings.Mode == TerrainProxyMode.Circle) ||
            (prefabHash == SlopePrefabHash && settings.Mode == TerrainProxyMode.Slope) ||
            (prefabHash == PaintPrefabHash && settings.Mode == TerrainProxyMode.Paint);
        return modeMatchesPrefab &&
               IsFinite(settings.Radius) &&
               IsFinite(settings.Width) &&
               IsFinite(settings.Length) &&
               IsFinite(settings.SlopeHeightDelta) &&
               IsFinite(settings.TerrainEdgeSoftness) &&
               settings.SearchRadius > 0f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return IsFinite(value.x) &&
               IsFinite(value.y) &&
               IsFinite(value.z) &&
               IsFinite(value.w);
    }

    private static bool TryPrepareTerrainReset(
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        TerrainResetChannels channels,
        out PreparedTerrainReset preparedReset,
        out string failureReason)
    {
        preparedReset = new PreparedTerrainReset(position, rotation, settings, channels);
        failureReason = string.Empty;
        List<Heightmap> heightmaps = [];
        Heightmap.FindHeightmap(position, settings.SearchRadius + SearchPadding, heightmaps);

        List<Heightmap> preparedHeightmaps = [];
        HashSet<TerrainComp> seen = [];
        bool includeCircleBoundary = settings.Mode != TerrainProxyMode.Paint;
        ZNetScene? scene = ZNetScene.instance;
        ZDOMan? world = ZDOMan.instance;
        foreach (Heightmap heightmap in heightmaps)
        {
            if (!heightmap ||
                heightmap.IsDistantLod ||
                !FootprintIntersectsHeightmap(
                    position,
                    rotation,
                    settings,
                    heightmap,
                    includeCircleBoundary))
            {
                continue;
            }

            if (scene == null || world == null || !scene.IsAreaReady(heightmap.transform.position))
            {
                failureReason = "a required terrain area is still streaming";
                return false;
            }

            TerrainComp compiler = heightmap.GetAndCreateTerrainCompiler();
            if (!compiler || !seen.Add(compiler) ||
                !TryInspectTerrainCompiler(compiler, out Heightmap preparedHeightmap, out int width) ||
                !ReferenceEquals(preparedHeightmap, heightmap))
            {
                failureReason = "a required terrain compiler is not ready";
                return false;
            }

            ZDO terrainZdo = compiler.m_nview.GetZDO();
            if (terrainZdo == null ||
                compiler.m_lastDataRevision != terrainZdo.DataRevision ||
                (!ZNet.instance.IsServer() && world.m_clientChangeQueue.Contains(terrainZdo.m_uid)))
            {
                failureReason = "a required terrain compiler is still synchronizing";
                return false;
            }

            preparedHeightmaps.Add(preparedHeightmap);
            preparedReset.Compilers.Add(new PreparedTerrainResetCompiler(compiler, preparedHeightmap, width));
        }

        if (!IsFootprintCoveredByHeightmaps(
                position,
                rotation,
                settings,
                heightmaps,
                preparedHeightmaps,
                includeCircleBoundary))
        {
            failureReason = "the complete terrain footprint is not loaded";
            return false;
        }

        return true;
    }

    private static TerrainResetChannels GetTerrainResetChannels(TerrainProxySettings settings)
    {
        TerrainResetChannels channels = TerrainResetChannels.None;
        if (settings.ModifiesHeight)
        {
            channels |= TerrainResetChannels.Height;
        }

        if (settings.ModifiesPaint)
        {
            channels |= TerrainResetChannels.Paint;
        }

        return channels;
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
        float rectLength,
        bool includeBoundary = true)
    {
        Vector3 local = InverseYaw(rectRotation) * (circlePosition - rectPosition);
        float x = Mathf.Clamp(local.x, rectWidth * -0.5f, rectWidth * 0.5f);
        float z = Mathf.Clamp(local.z, rectLength * -0.5f, rectLength * 0.5f);
        float dx = local.x - x;
        float dz = local.z - z;
        float distanceSquared = dx * dx + dz * dz;
        float radiusSquared = radius * radius;
        return includeBoundary
            ? distanceSquared <= radiusSquared
            : distanceSquared < radiusSquared;
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

    private static bool ModifyTerrainCompiler(
        TerrainComp compiler,
        Heightmap heightmap,
        int width,
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings)
    {
        bool changed = false;
        Quaternion inverseYaw = Quaternion.Inverse(Quaternion.Euler(0f, rotation.eulerAngles.y, 0f));
        if (!TryGetHeightmapIndexRange(heightmap, position, settings.SearchRadius + SearchPadding, out int minX, out int maxX, out int minZ, out int maxZ))
        {
            return false;
        }

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int index = z * width + x;
                Vector3 node = VertexToWorld(heightmap, x, z);
                bool affected = TryGetAffectedTerrainNode(
                    position,
                    inverseYaw,
                    settings,
                    node,
                    out Vector3 local,
                    out float terrainNormalized);

                if (settings.ModifiesHeight && affected)
                {
                    float targetHeight = settings.GetTargetHeight(position, heightmap.transform.position.y, local.z);
                    float height = heightmap.GetHeight(x, z);
                    changed |= ApplyHeightNode(compiler, index, height, targetHeight, local.x, local.z, settings);
                }

                if (settings.ModifiesPaint && affected && HasPaintInfluence(terrainNormalized))
                {
                    changed |= ApplyPaintNode(compiler, heightmap, x, z, index, terrainNormalized, settings);
                }
            }
        }

        return changed;
    }

    private static bool ResetTerrainCompiler(
        TerrainComp compiler,
        Heightmap heightmap,
        int width,
        Vector3 position,
        Quaternion rotation,
        TerrainProxySettings settings,
        TerrainResetChannels channels)
    {
        bool changed = false;
        bool resetHeight = (channels & TerrainResetChannels.Height) != 0;
        bool resetPaint = (channels & TerrainResetChannels.Paint) != 0;
        Quaternion inverseYaw = Quaternion.Inverse(Quaternion.Euler(0f, rotation.eulerAngles.y, 0f));
        if (!TryGetHeightmapIndexRange(heightmap, position, settings.SearchRadius + SearchPadding, out int minX, out int maxX, out int minZ, out int maxZ))
        {
            return false;
        }

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int index = z * width + x;
                Vector3 node = VertexToWorld(heightmap, x, z);
                if (!TryGetAffectedTerrainNode(
                        position,
                        inverseYaw,
                        settings,
                        node,
                        out _,
                        out float normalized))
                {
                    continue;
                }

                if (resetHeight &&
                    (compiler.m_modifiedHeight[index] ||
                     Mathf.Abs(compiler.m_levelDelta[index]) > 0.001f ||
                     Mathf.Abs(compiler.m_smoothDelta[index]) > 0.001f))
                {
                    compiler.m_modifiedHeight[index] = false;
                    compiler.m_levelDelta[index] = 0f;
                    compiler.m_smoothDelta[index] = 0f;
                    changed = true;
                }

                if (resetPaint &&
                    compiler.m_modifiedPaint[index] &&
                    (settings.Mode != TerrainProxyMode.Paint || HasPaintInfluence(normalized)))
                {
                    compiler.m_modifiedPaint[index] = false;
                    compiler.m_paintMask[index] = Color.clear;
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        CommitTerrainCompiler(compiler, heightmap, position, settings.SearchRadius);
        return true;
    }

    private static bool TryInspectTerrainCompiler(TerrainComp compiler, out Heightmap heightmap, out int width)
    {
        heightmap = null!;
        width = 0;
        if (!compiler.m_nview || !compiler.m_nview.IsValid() || !compiler.m_hmap)
        {
            return false;
        }

        heightmap = compiler.m_hmap;
        width = heightmap.m_width + 1;
        int count = width * width;
        return compiler.m_modifiedHeight != null &&
               compiler.m_levelDelta != null &&
               compiler.m_smoothDelta != null &&
               compiler.m_modifiedPaint != null &&
               compiler.m_paintMask != null &&
               compiler.m_modifiedHeight.Length == count &&
               compiler.m_levelDelta.Length == count &&
               compiler.m_smoothDelta.Length == count &&
               compiler.m_modifiedPaint.Length == count &&
               compiler.m_paintMask.Length == count;
    }

    private static bool TryClaimTerrainCompilerOwnership(TerrainComp compiler)
    {
        if (!compiler.m_nview || !compiler.m_nview.IsValid())
        {
            return false;
        }

        if (!compiler.m_nview.IsOwner())
        {
            compiler.m_nview.ClaimOwnership();
        }

        return compiler.m_nview.IsOwner();
    }

    private static void CommitTerrainCompiler(
        TerrainComp compiler,
        Heightmap heightmap,
        Vector3 position,
        float radius)
    {
        compiler.m_operations++;
        compiler.m_lastOpPoint = position;
        compiler.m_lastOpRadius = radius;
        compiler.Save();
        heightmap.Poke(delayed: false);
    }

    private static bool TryGetHeightmapIndexRange(
        Heightmap heightmap,
        Vector3 position,
        float radius,
        out int minX,
        out int maxX,
        out int minZ,
        out int maxZ)
    {
        int maxIndex = heightmap.m_width;
        float scale = Mathf.Max(heightmap.m_scale, 0.001f);
        float halfWidth = heightmap.m_width * 0.5f;
        Vector3 origin = heightmap.transform.position;

        minX = Mathf.Clamp(Mathf.FloorToInt((position.x - radius - origin.x) / scale + halfWidth) - 1, 0, maxIndex);
        maxX = Mathf.Clamp(Mathf.CeilToInt((position.x + radius - origin.x) / scale + halfWidth) + 1, 0, maxIndex);
        minZ = Mathf.Clamp(Mathf.FloorToInt((position.z - radius - origin.z) / scale + halfWidth) - 1, 0, maxIndex);
        maxZ = Mathf.Clamp(Mathf.CeilToInt((position.z + radius - origin.z) / scale + halfWidth) + 1, 0, maxIndex);

        return minX <= maxX && minZ <= maxZ;
    }

    private static bool ApplyHeightNode(
        TerrainComp compiler,
        int index,
        float currentHeight,
        float targetHeight,
        float localX,
        float localZ,
        TerrainProxySettings settings)
    {
        float delta = (targetHeight - currentHeight + compiler.m_smoothDelta[index]) * settings.GetLevelFalloff(localX, localZ);
        compiler.m_smoothDelta[index] = 0f;

        float previous = compiler.m_levelDelta[index];
        compiler.m_levelDelta[index] = Mathf.Clamp(previous + delta, -AdminTerrainMaxDelta, AdminTerrainMaxDelta);
        compiler.m_modifiedHeight[index] = Mathf.Abs(compiler.m_levelDelta[index]) > 0.001f;
        return Mathf.Abs(previous - compiler.m_levelDelta[index]) > 0.001f;
    }

    private static bool ApplyPaintNode(
        TerrainComp compiler,
        Heightmap heightmap,
        int x,
        int z,
        int index,
        float normalized,
        TerrainProxySettings settings)
    {
        if (normalized > 1f)
        {
            return false;
        }

        Color current = heightmap.GetPaintMask(x, z);
        Color target = GetPaintTarget(settings.PaintType, current);
        Color desired = Color.Lerp(current, target, Mathf.Pow(1f - Mathf.Clamp01(normalized), 0.1f));
        if (settings.PaintType != AdminTerrainPaintType.ClearVegetation)
        {
            desired.a = current.a;
        }

        if (compiler.m_modifiedPaint[index])
        {
            return !Approximately(compiler.m_paintMask[index], desired) && SetPaintNode(compiler, index, desired);
        }

        if (Approximately(current, desired))
        {
            return false;
        }

        return SetPaintNode(compiler, index, desired);
    }

    private static bool SetPaintNode(TerrainComp compiler, int index, Color desired)
    {
        compiler.m_modifiedPaint[index] = true;
        compiler.m_paintMask[index] = desired;
        return true;
    }

    private static Color GetPaintTarget(AdminTerrainPaintType paintType, Color current)
    {
        return paintType switch
        {
            AdminTerrainPaintType.Grass => Heightmap.m_paintMaskNothing,
            AdminTerrainPaintType.Dirt => Heightmap.m_paintMaskDirt,
            AdminTerrainPaintType.Cultivated => Heightmap.m_paintMaskCultivated,
            AdminTerrainPaintType.Paved => Heightmap.m_paintMaskPaved,
            AdminTerrainPaintType.DarkGrass => new Color(0.6f, 0.5f, 0f),
            AdminTerrainPaintType.PatchyGrass => new Color(0f, 0.75f, 0f),
            AdminTerrainPaintType.MossyPaving => new Color(0f, 0f, 0.5f),
            AdminTerrainPaintType.DirtPaving => new Color(1f, 0f, 0.5f),
            AdminTerrainPaintType.DarkPaving => new Color(0f, 1f, 0.5f),
            AdminTerrainPaintType.ClearVegetation => Heightmap.m_paintMaskClearVegetation,
            _ => current
        };
    }

    private static Vector3 VertexToWorld(Heightmap heightmap, int x, int z)
    {
        Vector3 position = heightmap.transform.position;
        position.x += (x - heightmap.m_width / 2f) * heightmap.m_scale;
        position.z += (z - heightmap.m_width / 2f) * heightmap.m_scale;
        return position;
    }

    private static bool TryGetAffectedTerrainNode(
        Vector3 center,
        Quaternion inverseYaw,
        TerrainProxySettings settings,
        Vector3 node,
        out Vector3 local,
        out float normalized)
    {
        local = inverseYaw * (node - center);
        normalized = settings.GetNormalizedDistance(local.x, local.z);
        return normalized <= 1f;
    }

    private static bool HasPaintInfluence(float normalized)
    {
        return normalized < 1f;
    }

    private static bool Approximately(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.001f &&
               Mathf.Abs(a.g - b.g) < 0.001f &&
               Mathf.Abs(a.b - b.b) < 0.001f &&
               Mathf.Abs(a.a - b.a) < 0.001f;
    }

    [Flags]
    private enum TerrainResetChannels
    {
        None = 0,
        Height = 1,
        Paint = 2
    }

    private sealed class PreparedTerrainReset
    {
        public PreparedTerrainReset(
            Vector3 position,
            Quaternion rotation,
            TerrainProxySettings settings,
            TerrainResetChannels channels)
        {
            Position = position;
            Rotation = rotation;
            Settings = settings;
            Channels = channels;
        }

        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public TerrainProxySettings Settings { get; }
        public TerrainResetChannels Channels { get; }
        public List<PreparedTerrainResetCompiler> Compilers { get; } = [];
    }

    private readonly struct LoadedProxyResetCandidate
    {
        public LoadedProxyResetCandidate(
            ZDO zdo,
            ZNetView view,
            Vector3 position,
            Quaternion rotation,
            TerrainProxySettings settings,
            TerrainResetChannels channels)
        {
            Zdo = zdo;
            View = view;
            Position = position;
            Rotation = rotation;
            Settings = settings;
            Channels = channels;
        }

        public ZDO Zdo { get; }
        public ZNetView View { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public TerrainProxySettings Settings { get; }
        public TerrainResetChannels Channels { get; }
    }

    private sealed class ReplayTerrainData
    {
        public ReplayTerrainData(
            int operations,
            Vector3 lastOpPoint,
            float lastOpRadius,
            bool[] modifiedHeight,
            float[] levelDelta,
            float[] smoothDelta,
            bool[] modifiedPaint,
            Color[] paintMask,
            byte[] trailingData,
            bool hasCheckpoint,
            long checkpointBatch,
            int checkpointIndex)
        {
            Operations = operations;
            LastOpPoint = lastOpPoint;
            LastOpRadius = lastOpRadius;
            ModifiedHeight = modifiedHeight;
            LevelDelta = levelDelta;
            SmoothDelta = smoothDelta;
            ModifiedPaint = modifiedPaint;
            PaintMask = paintMask;
            TrailingData = trailingData;
            HasCheckpoint = hasCheckpoint;
            CheckpointBatch = checkpointBatch;
            CheckpointIndex = checkpointIndex;
        }

        public int Operations { get; }
        public Vector3 LastOpPoint { get; }
        public float LastOpRadius { get; }
        public bool[] ModifiedHeight { get; }
        public float[] LevelDelta { get; }
        public float[] SmoothDelta { get; }
        public bool[] ModifiedPaint { get; }
        public Color[] PaintMask { get; }
        public byte[] TrailingData { get; }
        public bool HasCheckpoint { get; }
        public long CheckpointBatch { get; }
        public int CheckpointIndex { get; }
        public int HeightCount => ModifiedHeight.Length;
        public int PaintCount => ModifiedPaint.Length;

        public static ReplayTerrainData CreateEmpty(int terrainNodes)
        {
            return new ReplayTerrainData(
                0,
                Vector3.zero,
                0f,
                new bool[terrainNodes],
                new float[terrainNodes],
                new float[terrainNodes],
                new bool[terrainNodes],
                new Color[terrainNodes],
                [],
                false,
                0L,
                -1);
        }
    }

    private sealed class TerrainCompilerSnapshot
    {
        private readonly int _operations;
        private readonly Vector3 _lastOpPoint;
        private readonly float _lastOpRadius;
        private readonly bool[] _modifiedHeight;
        private readonly float[] _levelDelta;
        private readonly float[] _smoothDelta;
        private readonly bool[] _modifiedPaint;
        private readonly Color[] _paintMask;

        public TerrainCompilerSnapshot(TerrainComp compiler)
        {
            _operations = compiler.m_operations;
            _lastOpPoint = compiler.m_lastOpPoint;
            _lastOpRadius = compiler.m_lastOpRadius;
            _modifiedHeight = (bool[])compiler.m_modifiedHeight.Clone();
            _levelDelta = (float[])compiler.m_levelDelta.Clone();
            _smoothDelta = (float[])compiler.m_smoothDelta.Clone();
            _modifiedPaint = (bool[])compiler.m_modifiedPaint.Clone();
            _paintMask = (Color[])compiler.m_paintMask.Clone();
        }

        public void Restore(TerrainComp compiler)
        {
            compiler.m_operations = _operations;
            compiler.m_lastOpPoint = _lastOpPoint;
            compiler.m_lastOpRadius = _lastOpRadius;
            Array.Copy(_modifiedHeight, compiler.m_modifiedHeight, _modifiedHeight.Length);
            Array.Copy(_levelDelta, compiler.m_levelDelta, _levelDelta.Length);
            Array.Copy(_smoothDelta, compiler.m_smoothDelta, _smoothDelta.Length);
            Array.Copy(_modifiedPaint, compiler.m_modifiedPaint, _modifiedPaint.Length);
            Array.Copy(_paintMask, compiler.m_paintMask, _paintMask.Length);
        }
    }

    private readonly struct PreparedTerrainResetCompiler
    {
        public PreparedTerrainResetCompiler(TerrainComp compiler, Heightmap heightmap, int width)
        {
            Compiler = compiler;
            Heightmap = heightmap;
            Width = width;
        }

        public TerrainComp Compiler { get; }
        public Heightmap Heightmap { get; }
        public int Width { get; }
    }
}

[HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.ApplyToHeightmap))]
internal static class AdminTerrainToolTerrainCompPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo limit = AccessTools.Method(typeof(AdminTerrainTool), nameof(AdminTerrainTool.GetTerrainCompHeightClampLimit));
        if (limit == null)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning(
                "ZoneSavior terrain clamp patch skipped because the replacement method could not be resolved.");
            return code;
        }

        const int expectedMatches = 2;
        List<CodeInstruction> matches = code
            .Where(instruction =>
                instruction.opcode == OpCodes.Ldc_R4 &&
                instruction.operand is float value &&
                Math.Abs(value - 8f) < 0.001f)
            .ToList();
        if (matches.Count != expectedMatches)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning(
                $"ZoneSavior terrain clamp patch expected {expectedMatches} clamp constants but found {matches.Count}; leaving original IL unchanged.");
            return code;
        }

        foreach (CodeInstruction instruction in matches)
        {
            instruction.opcode = OpCodes.Call;
            instruction.operand = limit;
        }

        return code;
    }
}

internal static class AdminTerrainToolZNetDiagnostics
{
    private const int MaxLoggedZdos = 32;
    private const int ScanIntervalFrames = 30;

    private static readonly HashSet<ZDOID> LoggedDestroyZdos = [];
    private static readonly HashSet<ZDOID> LoggedRemoveZdos = [];
    private static ZNetScene? _diagnosticScene;
    private static int _nextScanFrame;

    public static void InspectDestroyingView(ZNetView view)
    {
        try
        {
            ZNetScene scene = ZNetScene.instance;
            ZDO zdo = view.m_zdo;
            if (scene == null || zdo == null)
            {
                return;
            }

            EnsureScene(scene);
            if (!scene.m_instances.TryGetValue(zdo, out ZNetView registeredView) ||
                !ReferenceEquals(registeredView, view))
            {
                return;
            }

            LogStaleZdo(
                scene,
                zdo,
                $"registered ZNetView is being destroyed without prior unregister; viewGhost={view.m_ghost}",
                LoggedDestroyZdos);
        }
        catch (Exception ex)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogDebug($"ZoneSavior ZNet diagnostic could not inspect OnDestroy: {ex.Message}");
        }
    }

    public static void InspectRegisteredViews(
        ZNetScene scene,
        List<ZDO> currentNearObjects,
        List<ZDO> currentDistantObjects)
    {
        EnsureScene(scene);
        if (LoggedRemoveZdos.Count >= MaxLoggedZdos || Time.frameCount < _nextScanFrame)
        {
            return;
        }

        _nextScanFrame = Time.frameCount + ScanIntervalFrames;
        try
        {
            foreach (KeyValuePair<ZDO, ZNetView> entry in scene.m_instances)
            {
                ZNetView view = entry.Value;
                if (!ReferenceEquals(view, null) && view)
                {
                    continue;
                }

                LogStaleZdo(
                    scene,
                    entry.Key,
                    ReferenceEquals(view, null)
                        ? $"m_instances contains a CLR-null ZNetView before RemoveObjects; " +
                          $"near={currentNearObjects.Contains(entry.Key)}, distant={currentDistantObjects.Contains(entry.Key)}"
                        : $"m_instances contains a destroyed Unity ZNetView before RemoveObjects; " +
                          $"near={currentNearObjects.Contains(entry.Key)}, distant={currentDistantObjects.Contains(entry.Key)}",
                    LoggedRemoveZdos);

                if (LoggedRemoveZdos.Count >= MaxLoggedZdos)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogDebug($"ZoneSavior ZNet diagnostic could not inspect m_instances: {ex.Message}");
        }
    }

    private static void EnsureScene(ZNetScene scene)
    {
        if (ReferenceEquals(_diagnosticScene, scene))
        {
            return;
        }

        _diagnosticScene = scene;
        LoggedDestroyZdos.Clear();
        LoggedRemoveZdos.Clear();
        _nextScanFrame = 0;
    }

    private static void LogStaleZdo(
        ZNetScene scene,
        ZDO zdo,
        string reason,
        HashSet<ZDOID> loggedZdos)
    {
        if (zdo == null || loggedZdos.Count >= MaxLoggedZdos || !loggedZdos.Add(zdo.m_uid))
        {
            return;
        }

        int prefabHash = zdo.GetPrefab();
        GameObject prefab = scene.GetPrefab(prefabHash);
        string prefabName = prefab ? prefab.name : "<unknown>";
        Vector3 position = zdo.GetPosition();
        ZDO? pending = ZNetView.m_initZDO;
        string pendingUid = pending == null ? "none" : pending.m_uid.ToString();

        ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning(
            $"ZoneSavior ZNet diagnostic: {reason}. " +
            $"zdo={zdo.m_uid}, prefab={prefabName} ({prefabHash}), " +
            $"position=({position.x:0.##}, {position.y:0.##}, {position.z:0.##}), " +
            $"created={zdo.Created}, persistent={zdo.Persistent}, distant={zdo.Distant}, type={zdo.Type}, " +
            $"ghostInit={ZNetView.m_ghostInit}, useInitZDO={ZNetView.m_useInitZDO}, " +
            $"pendingZdo={pendingUid}, registeredInstances={scene.m_instances.Count}, frame={Time.frameCount}.");
    }
}

[HarmonyPatch(typeof(ZNetView), "OnDestroy")]
internal static class AdminTerrainToolZNetViewDestroyDiagnosticPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(ZNetView __instance)
    {
        AdminTerrainToolZNetDiagnostics.InspectDestroyingView(__instance);
    }
}

[HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
internal static class AdminTerrainToolZNetSceneRemoveDiagnosticPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(
        ZNetScene __instance,
        List<ZDO> __0,
        List<ZDO> __1)
    {
        AdminTerrainToolZNetDiagnostics.InspectRegisteredViews(
            __instance,
            __0,
            __1);
    }
}

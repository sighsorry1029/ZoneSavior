using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static ZoneSavior.ZoneBundleTerrainGrid;

namespace ZoneSavior;

internal static partial class ZoneBundleTerrain
{
    private const float SearchRadius = 48f;
    private const float SupportFillSampleStep = 1f;
    private const float SupportFillClearance = 0.05f;
    private const float FallbackSupportPlaneQuantization = 0.25f;
    private const float FallbackColliderSampleStep = 0.5f;
    private const float FallbackMaxColliderDepthBelowOrigin = 8f;
    private const float FallbackMaxTerrainDelta = 16f;
    private const float NativeMaxTerrainDelta = 8f;
    private const int TerrainContextEntryBatchSize = 128;
    private const int TerrainApplyNodeBatchSize = 1024;

    public static IEnumerator ComputeSupportAnchorAsync(IEnumerable<Vector2i> zones, Action<TerrainSourceAnchor> onComplete)
    {
        float min = float.PositiveInfinity;
        float tamedFallbackMin = float.PositiveInfinity;

        foreach (Vector2i zone in zones)
        {
            AccumulateSupportAnchor(zone, ref min, ref tamedFallbackMin);
            yield return null;
        }

        onComplete(CreateTerrainSourceAnchor(min, tamedFallbackMin));
    }

    private static void AccumulateSupportAnchor(Vector2i zone, ref float min, ref float tamedFallbackMin)
    {
        if (ZDOMan.instance == null || ZNetScene.instance == null)
        {
            return;
        }

        List<ZDO> objects = [];
        ZDOMan.instance.FindObjects(zone, objects);
        foreach (ZDO zdo in objects)
        {
            if (TryReadSupportWearNTear(zdo, zone, ZoneBundleConfig.WearNTearSaveMode, out GameObject prefab) &&
                TryGetWearNTearWorldBounds(prefab, zdo.GetPosition(), zdo.GetRotation(), ReadScale(zdo, prefab), out Bounds bounds) &&
                IsReasonableFallbackBoundsMinimum(zdo.GetPosition().y, bounds.min.y))
            {
                min = Mathf.Min(min, bounds.min.y);
            }

            if (TryReadTamedMonster(zdo, zone, out _))
            {
                tamedFallbackMin = Mathf.Min(tamedFallbackMin, zdo.GetPosition().y);
            }
        }
    }

    private static TerrainSourceAnchor CreateTerrainSourceAnchor(float min, float tamedFallbackMin)
    {
        if (!float.IsPositiveInfinity(min))
        {
            return new TerrainSourceAnchor(min);
        }

        return float.IsPositiveInfinity(tamedFallbackMin)
            ? new TerrainSourceAnchor(float.NaN)
            : new TerrainSourceAnchor(tamedFallbackMin);
    }

    public static IEnumerator CreateSupportFillPlacementContextAsync(IEnumerable<TerrainSupportTarget> targets, Action<TerrainPlacementContext?> onComplete)
    {
        List<PlacementSupportSampleSet> sampleSets = [];
        foreach (TerrainSupportTarget target in targets.ToList())
        {
            bool usesSavedContacts = target.ContactsCaptured && target.Contacts.Count > 0;
            List<TerrainSupportSample> samples = [];
            if (usesSavedContacts)
            {
                samples = CollectSavedContactSamples(target.Zone, target.Contacts);
            }
            else
            {
                yield return CollectSupportSamplesAsync(target.Zone, target.Entries, target.SourceBaseY, value => samples = value);
            }

            if (samples.Count > 0)
            {
                sampleSets.Add(new PlacementSupportSampleSet(usesSavedContacts, samples));
            }

            yield return null;
        }

        onComplete(BuildSupportFillPlacementContext(sampleSets));
    }

    private static TerrainPlacementContext? BuildSupportFillPlacementContext(List<PlacementSupportSampleSet> sampleSets)
    {
        List<TerrainSupportSample> samples = sampleSets.SelectMany(set => set.Samples).ToList();
        if (samples.Count == 0)
        {
            return null;
        }

        List<TerrainSupportSample> footprintSamples = CollapseToLowestSupportSamples(samples);
        bool hasSavedContacts = sampleSets.Any(set => set.UsesSavedContacts);
        float baseWorldY = hasSavedContacts
            ? ResolveSupportFillBaseWorldY(footprintSamples)
            : ResolveFallbackSupportBaseWorldY(footprintSamples);

        TerrainPlacementContext context = new()
        {
            BaseWorldY = baseWorldY
        };

        foreach (TerrainSupportSample sample in footprintSamples)
        {
            if (!hasSavedContacts && !IsReasonableFallbackSupportTarget(sample, baseWorldY))
            {
                continue;
            }

            int x = Mathf.RoundToInt(sample.WorldX);
            int z = Mathf.RoundToInt(sample.WorldZ);
            context.SupportRelativeHeights[PackCell(x, z)] = sample.RelativeY;
        }

        return context;
    }

    public static TerrainPlacementContext CreateExactContext(float sourceBaseY)
    {
        return new TerrainPlacementContext
        {
            BaseWorldY = sourceBaseY
        };
    }

    public static List<ZoneBundleTerrainContact> CaptureSupportContacts(Vector2i zone, float sourceBaseY, IEnumerable<ZoneBundleEntry> entries, out bool contactsCaptured)
    {
        contactsCaptured = false;
        List<ZoneBundleTerrainContact> contacts = [];
        if (float.IsNaN(sourceBaseY) || !TryGetHeightmap(zone, out _))
        {
            return contacts;
        }

        contactsCaptured = true;
        List<TerrainWorldContact> worldContacts = ZoneTerrainContactSampler.CaptureWorldContacts(
            ZoneTerrainContactSampler.FromZoneEntries(zone, sourceBaseY, entries),
            ZoneBundleConfig.SupportFillContactTolerance);
        return ZoneTerrainContactSampler.ToZoneBundleContacts(zone, sourceBaseY, worldContacts);
    }

    public static bool HasApplicableSupportFill(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IEnumerable<ZoneBundleTerrainContact> contacts,
        bool contactsCaptured,
        TerrainPlacementContext context)
    {
        return CreateSupportPlan(
            zone,
            entries,
            contacts,
            contactsCaptured,
            context,
            out _).HasSupport;
    }

    public static IEnumerator ApplySupportFillAsync(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IEnumerable<ZoneBundleTerrainContact> contacts,
        bool contactsCaptured,
        TerrainPlacementContext context,
        Action<bool> onComplete)
    {
        TerrainSupportApplicationPlan plan = CreateSupportPlan(
            zone,
            entries,
            contacts,
            contactsCaptured,
            context,
            out Heightmap heightmap);

        if (!plan.HasSupport)
        {
            onComplete(false);
            yield break;
        }

        bool changed = false;
        yield return ApplySupportCellsToHeightmapAsync(
            heightmap,
            plan.SupportHeights,
            plan.SupportCells,
            ZoneBundleConfig.SupportFillFeatherWidth,
            result => changed = result);
        onComplete(changed);
    }

    public static bool IsSupportWearNTear(ZDO zdo, Vector2i zone, out GameObject prefab)
    {
        return TryReadSupportWearNTear(zdo, zone, ZoneBundleConfig.WearNTearSaveMode, out prefab);
    }

    public static bool CanApply(Vector2i zone)
    {
        return IsZoneLoaded(zone) && TryGetHeightmap(zone, out _);
    }

    private static TerrainSupportApplicationPlan CreateSupportPlan(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IEnumerable<ZoneBundleTerrainContact> contacts,
        bool contactsCaptured,
        TerrainPlacementContext context,
        out Heightmap heightmap)
    {
        List<ZoneBundleTerrainContact> contactList = contacts.ToList();
        bool hasContacts = contactsCaptured && contactList.Count > 0;

        if (!TryGetHeightmap(zone, out heightmap))
        {
            throw new InvalidOperationException($"Target zone ({zone.x},{zone.y}) is not loaded for support terrain placement.");
        }

        return BuildSupportPlan(
            zone,
            entries,
            contactList,
            hasContacts,
            context,
            heightmap,
            ZoneBundleConfig.SupportFillFeatherWidth);
    }

    private static TerrainSupportApplicationPlan BuildSupportPlan(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IReadOnlyCollection<ZoneBundleTerrainContact> contacts,
        bool hasContacts,
        TerrainPlacementContext context,
        Heightmap heightmap,
        float featherWidth)
    {
        Dictionary<long, float> supportHeights = [];
        if (context.SupportRelativeHeights.Count > 0)
        {
            AddContextSupportHeights(context, heightmap, featherWidth, supportHeights);
        }
        else
        {
            IEnumerable<TerrainSupportSample> samples = hasContacts
                ? CollectSavedContactSamples(zone, contacts)
                : CollectSupportSamples(zone, entries);
            foreach (TerrainSupportSample sample in samples)
            {
                float targetHeight = context.BaseWorldY + sample.RelativeY - SupportFillClearance;
                if (!hasContacts && !IsReasonableFallbackTarget(sample.WorldX, sample.WorldZ, targetHeight))
                {
                    continue;
                }

                AddSupportHeight(
                    Mathf.RoundToInt(sample.WorldX),
                    Mathf.RoundToInt(sample.WorldZ),
                    targetHeight,
                    supportHeights);
            }
        }

        return new TerrainSupportApplicationPlan(supportHeights, ToSupportCells(supportHeights));
    }

    private static void AddContextSupportHeights(
        TerrainPlacementContext context,
        Heightmap heightmap,
        float featherWidth,
        Dictionary<long, float> supportHeights)
    {
        GetHeightmapWorldBounds(heightmap, featherWidth + 1f, out float minX, out float maxX, out float minZ, out float maxZ);
        foreach (KeyValuePair<long, float> item in context.SupportRelativeHeights)
        {
            UnpackCell(item.Key, out int x, out int z);
            if (x < minX || x > maxX || z < minZ || z > maxZ)
            {
                continue;
            }

            float targetHeight = context.BaseWorldY + item.Value - SupportFillClearance;
            AddSupportHeight(x, z, targetHeight, supportHeights);
        }
    }

    private static List<TerrainSupportCell> ToSupportCells(Dictionary<long, float> supportHeights)
    {
        List<TerrainSupportCell> supportCells = [];
        foreach (KeyValuePair<long, float> item in supportHeights)
        {
            UnpackCell(item.Key, out int x, out int z);
            supportCells.Add(new TerrainSupportCell(x, z, item.Value));
        }

        return supportCells;
    }

    private static void AddSupportHeight(int x, int z, float targetHeight, Dictionary<long, float> supportHeights)
    {
        long key = PackCell(x, z);
        if (!supportHeights.TryGetValue(key, out float existing) || targetHeight < existing)
        {
            supportHeights[key] = targetHeight;
        }
    }

    private static void GetHeightmapWorldBounds(Heightmap heightmap, float padding, out float minX, out float maxX, out float minZ, out float maxZ)
    {
        Vector3 center = heightmap.transform.position;
        float half = heightmap.m_width * heightmap.m_scale * 0.5f;
        minX = center.x - half - padding;
        maxX = center.x + half + padding;
        minZ = center.z - half - padding;
        maxZ = center.z + half + padding;
    }

    public static bool TryGetTerrainHeight(float x, float z, out float height)
    {
        return TryGetCurrentTerrainHeight(x, z, out height);
    }

    private static bool TryGetFeatheredSupportHeight(Vector3 node, float baseHeight, TerrainSupportCellIndex supportIndex, float featherWidth, out float height)
    {
        height = baseHeight;
        if (featherWidth <= 0f)
        {
            return false;
        }

        float maxDistanceSqr = featherWidth * featherWidth;
        if (!supportIndex.TryGetNearest(node, maxDistanceSqr, out TerrainSupportCell nearest, out float bestDistanceSqr))
        {
            return false;
        }

        float distance = Mathf.Sqrt(bestDistanceSqr);
        float weight = 1f - Mathf.Clamp01(distance / featherWidth);
        weight = weight * weight * (3f - 2f * weight);
        height = Mathf.Lerp(baseHeight, nearest.Height, weight);
        return true;
    }

    private static IEnumerator ApplySupportCellsToHeightmapAsync(
        Heightmap heightmap,
        Dictionary<long, float> supportHeights,
        List<TerrainSupportCell> supportCells,
        float featherWidth,
        Action<bool> onComplete)
    {
        int width = heightmap.m_width + 1;
        float[] worldHeights = new float[width * width];
        TerrainSupportCellIndex supportIndex = new(supportCells, featherWidth);
        bool changed = false;
        int processed = 0;

        for (int z = 0; z < width; z++)
        {
            for (int x = 0; x < width; x++)
            {
                changed |= ComputeSupportNode(heightmap, width, x, z, supportHeights, supportIndex, featherWidth, worldHeights);
                processed++;
                if (processed >= TerrainApplyNodeBatchSize)
                {
                    processed = 0;
                    yield return null;
                }
            }
        }

        if (!changed)
        {
            onComplete(false);
            yield break;
        }

        PersistAppliedSupportCells(heightmap, width, worldHeights);
        onComplete(true);
    }

    private static bool ComputeSupportNode(
        Heightmap heightmap,
        int width,
        int x,
        int z,
        Dictionary<long, float> supportHeights,
        TerrainSupportCellIndex supportIndex,
        float featherWidth,
        float[] worldHeights)
    {
        int index = z * width + x;
        Vector3 node = VertexToWorld(heightmap, x, z);
        float current = GetWorldHeight(heightmap, x, z);
        float baseHeight = TryGetTerrainBaseHeight(node.x, node.z, out float terrainBaseHeight) ? terrainBaseHeight : current;
        float desired = baseHeight;
        if (supportHeights.TryGetValue(PackCell(Mathf.RoundToInt(node.x), Mathf.RoundToInt(node.z)), out float targetHeight))
        {
            desired = ClampTerrainDelta(targetHeight, baseHeight);
        }
        else if (TryGetFeatheredSupportHeight(node, baseHeight, supportIndex, featherWidth, out float featheredHeight))
        {
            desired = ClampTerrainDelta(featheredHeight, baseHeight);
        }

        worldHeights[index] = desired;
        return Mathf.Abs(current - desired) > 0.01f;
    }

    private static bool PersistAppliedSupportCells(
        Heightmap heightmap,
        int width,
        float[] worldHeights)
    {
        TerrainComp compiler = heightmap.GetAndCreateTerrainCompiler();
        PersistSupportFillTerrain(compiler, width, worldHeights);
        heightmap.Poke(delayed: false);
        ClutterSystem.instance?.ResetGrass(heightmap.transform.position, SearchRadius);
        return true;
    }

    private static void PersistSupportFillTerrain(
        TerrainComp compiler,
        int width,
        float[] worldHeights)
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

        for (int index = 0; index < count; index++)
        {
            int z = index / width;
            int x = index - z * width;
            float nativeHeight = GetTerrainBaseOrCurrentHeight(compiler.m_hmap, x, z);
            float delta = Mathf.Clamp(worldHeights[index] - nativeHeight, -NativeMaxTerrainDelta, NativeMaxTerrainDelta);
            compiler.m_smoothDelta[index] = 0f;
            compiler.m_levelDelta[index] = delta;
            compiler.m_modifiedHeight[index] = Mathf.Abs(delta) > 0.01f;
        }

        PersistCompiler(compiler);
    }

    private static float ClampTerrainDelta(float desired, float nativeHeight)
    {
        return Mathf.Clamp(
            desired,
            nativeHeight - NativeMaxTerrainDelta,
            nativeHeight + NativeMaxTerrainDelta);
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

    private static float GetWorldHeight(Heightmap heightmap, int x, int z)
    {
        return heightmap.GetHeight(x, z) + heightmap.transform.position.y;
    }

    private static float GetTerrainBaseOrCurrentHeight(Heightmap heightmap, int x, int z)
    {
        Vector3 node = VertexToWorld(heightmap, x, z);
        return TryGetTerrainBaseHeight(node.x, node.z, out float terrainBaseHeight)
            ? terrainBaseHeight
            : GetWorldHeight(heightmap, x, z);
    }

    private static bool TryGetTerrainBaseHeight(float x, float z, out float height)
    {
        if (WorldGenerator.instance != null)
        {
            height = WorldGenerator.instance.GetHeight(x, z);
            return true;
        }

        if (Heightmap.GetHeight(new Vector3(x, 0f, z), out height))
        {
            return true;
        }

        height = 0f;
        return false;
    }

    private static bool TryGetCurrentTerrainHeight(float x, float z, out float height)
    {
        return Heightmap.GetHeight(new Vector3(x, 0f, z), out height);
    }

    private static bool TryGetHeightmap(Vector2i zone, out Heightmap heightmap)
    {
        heightmap = null!;
        if (!IsZoneLoaded(zone))
        {
            return false;
        }

        heightmap = Heightmap.FindHeightmap(ZoneSystem.GetZonePos(zone));
        return heightmap != null;
    }

    private static bool IsZoneLoaded(Vector2i zone)
    {
        return ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(zone);
    }

    private static bool IsCompilerReady(TerrainComp compiler)
    {
        if (compiler == null || compiler.m_hmap == null)
        {
            return false;
        }

        ZNetView nview = compiler.GetComponent<ZNetView>();
        return nview != null && nview.IsValid();
    }

    private sealed class TerrainSupportApplicationPlan
    {
        public TerrainSupportApplicationPlan(Dictionary<long, float> supportHeights, List<TerrainSupportCell> supportCells)
        {
            SupportHeights = supportHeights;
            SupportCells = supportCells;
        }

        public Dictionary<long, float> SupportHeights { get; }
        public List<TerrainSupportCell> SupportCells { get; }
        public bool HasSupport => SupportHeights.Count > 0;
    }

    private readonly struct TerrainSupportCell
    {
        public TerrainSupportCell(int x, int z, float height)
        {
            X = x;
            Z = z;
            Height = height;
        }

        public int X { get; }
        public int Z { get; }
        public float Height { get; }
    }

    private sealed class TerrainSupportCellIndex
    {
        private readonly Dictionary<long, List<TerrainSupportCell>> _cellsByBucket = [];
        private readonly float _bucketSize;
        private readonly int _searchRadius;

        public TerrainSupportCellIndex(IEnumerable<TerrainSupportCell> cells, float featherWidth)
        {
            _bucketSize = Mathf.Max(1f, featherWidth);
            _searchRadius = Mathf.Max(0, Mathf.CeilToInt(featherWidth / _bucketSize));
            foreach (TerrainSupportCell cell in cells)
            {
                long key = PackCell(ToBucket(cell.X), ToBucket(cell.Z));
                if (!_cellsByBucket.TryGetValue(key, out List<TerrainSupportCell> bucket))
                {
                    bucket = [];
                    _cellsByBucket[key] = bucket;
                }

                bucket.Add(cell);
            }
        }

        public bool TryGetNearest(Vector3 node, float maxDistanceSqr, out TerrainSupportCell nearest, out float bestDistanceSqr)
        {
            nearest = default;
            bestDistanceSqr = float.PositiveInfinity;
            if (_cellsByBucket.Count == 0)
            {
                return false;
            }

            int bucketX = ToBucket(node.x);
            int bucketZ = ToBucket(node.z);
            for (int z = bucketZ - _searchRadius; z <= bucketZ + _searchRadius; z++)
            {
                for (int x = bucketX - _searchRadius; x <= bucketX + _searchRadius; x++)
                {
                    if (!_cellsByBucket.TryGetValue(PackCell(x, z), out List<TerrainSupportCell> bucket))
                    {
                        continue;
                    }

                    foreach (TerrainSupportCell cell in bucket)
                    {
                        float dx = node.x - cell.X;
                        float dz = node.z - cell.Z;
                        float distanceSqr = dx * dx + dz * dz;
                        if (distanceSqr >= bestDistanceSqr || distanceSqr > maxDistanceSqr)
                        {
                            continue;
                        }

                        bestDistanceSqr = distanceSqr;
                        nearest = cell;
                    }
                }
            }

            return !float.IsPositiveInfinity(bestDistanceSqr);
        }

        private int ToBucket(float value)
        {
            return Mathf.FloorToInt(value / _bucketSize);
        }
    }

    internal readonly struct TerrainSourceAnchor
    {
        public TerrainSourceAnchor(float baseWorldY)
        {
            BaseWorldY = baseWorldY;
        }

        public float BaseWorldY { get; }
    }
}

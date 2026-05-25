using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static ZoneSavior.ZoneBundleTerrainGrid;

namespace ZoneSavior;

internal static partial class ZoneBundleTerrain
{
    internal const string SupportFillMode = "support-fill-v1";

    private const float SearchRadius = 48f;
    private const float SupportFillSampleStep = 1f;
    private const float SupportFillClearance = 0.05f;
    private const float FallbackSupportPlaneQuantization = 0.25f;
    private const float FallbackColliderSampleStep = 0.5f;
    private const float FallbackMaxColliderDepthBelowOrigin = 8f;
    private const float FallbackMaxTerrainDelta = 16f;
    private const int TerrainContextEntryBatchSize = 128;
    private const int TerrainApplyNodeBatchSize = 1024;
    private static readonly int SupportFillBaseLayerHash = StringExtensionMethods.GetStableHashCode(ZoneSaviorPlugin.ModGUID + ".terrain_base_v1");
    private static readonly TerrainSupportStrategy SavedContactStrategy = new SavedContactTerrainStrategy();
    private static readonly TerrainSupportStrategy ColliderFallbackStrategy = new ColliderFallbackTerrainStrategy();

    public static TerrainSourceAnchor ComputeSupportAnchor(IEnumerable<Vector2i> zones)
    {
        float min = float.PositiveInfinity;
        float tamedFallbackMin = float.PositiveInfinity;

        foreach (Vector2i zone in zones)
        {
            AccumulateSupportAnchor(zone, ref min, ref tamedFallbackMin);
        }

        return CreateTerrainSourceAnchor(min, tamedFallbackMin);
    }

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

    public static TerrainPlacementContext? CreateSupportFillPlacementContext(IEnumerable<TerrainSupportTarget> targets)
    {
        List<TerrainSupportTarget> targetList = targets.ToList();
        List<PlacementSupportSampleSet> sampleSets = targetList
            .Select(target =>
            {
                TerrainSupportStrategy strategy = SelectPlacementStrategy(target);
                return new PlacementSupportSampleSet(strategy, strategy.CollectPlacementSamples(target));
            })
            .Where(set => set.Samples.Count > 0)
            .ToList();

        return BuildSupportFillPlacementContext(sampleSets);
    }

    public static IEnumerator CreateSupportFillPlacementContextAsync(IEnumerable<TerrainSupportTarget> targets, Action<TerrainPlacementContext?> onComplete)
    {
        List<PlacementSupportSampleSet> sampleSets = [];
        foreach (TerrainSupportTarget target in targets.ToList())
        {
            TerrainSupportStrategy strategy = SelectPlacementStrategy(target);
            List<TerrainSupportSample> samples = [];
            if (strategy == ColliderFallbackStrategy)
            {
                yield return CollectSupportSamplesAsync(target.Zone, target.Entries, target.SourceBaseY, value => samples = value);
            }
            else
            {
                samples = strategy.CollectPlacementSamples(target);
            }

            if (samples.Count > 0)
            {
                sampleSets.Add(new PlacementSupportSampleSet(strategy, samples));
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
        bool hasSavedContacts = sampleSets.Any(set => set.Strategy == SavedContactStrategy);
        TerrainSupportStrategy baseStrategy = hasSavedContacts ? SavedContactStrategy : ColliderFallbackStrategy;
        float baseWorldY = baseStrategy.ResolveBaseWorldY(footprintSamples);

        TerrainPlacementContext context = new()
        {
            BaseWorldY = baseWorldY
        };

        foreach (TerrainSupportSample sample in footprintSamples)
        {
            if (!hasSavedContacts && !ColliderFallbackStrategy.IsPlacementTargetUsable(sample, baseWorldY))
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

    public static bool ApplySupportFill(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IEnumerable<ZoneBundleTerrainContact> contacts,
        bool contactsCaptured,
        TerrainPlacementContext context)
    {
        TerrainSupportApplicationPlan plan = CreateSupportPlan(
            zone,
            entries,
            contacts,
            contactsCaptured,
            context,
            out Heightmap heightmap,
            out TerrainSupportApplyOptions applyOptions);
        return plan.HasSupport &&
               ApplySupportCellsToHeightmap(heightmap, plan.SupportHeights, plan.SupportCells, applyOptions);
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
            out _,
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
            out Heightmap heightmap,
            out TerrainSupportApplyOptions applyOptions);

        if (!plan.HasSupport)
        {
            onComplete(false);
            yield break;
        }

        bool changed = false;
        yield return ApplySupportCellsToHeightmapAsync(heightmap, plan.SupportHeights, plan.SupportCells, applyOptions, result => changed = result);
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
        out Heightmap heightmap,
        out TerrainSupportApplyOptions applyOptions)
    {
        List<ZoneBundleTerrainContact> contactList = contacts.ToList();
        bool hasContacts = contactsCaptured && contactList.Count > 0;
        applyOptions = TerrainSupportApplyOptions.ZoneBundle();

        if (!TryGetHeightmap(zone, out heightmap))
        {
            throw new InvalidOperationException($"Target zone ({zone.x},{zone.y}) is not loaded for support terrain placement.");
        }

        return BuildSupportPlan(zone, entries, contactList, hasContacts, context, heightmap, applyOptions);
    }

    private static TerrainSupportApplicationPlan BuildSupportPlan(
        Vector2i zone,
        IEnumerable<ZoneBundleEntry> entries,
        IReadOnlyCollection<ZoneBundleTerrainContact> contacts,
        bool hasContacts,
        TerrainPlacementContext context,
        Heightmap heightmap,
        TerrainSupportApplyOptions applyOptions)
    {
        Dictionary<long, float> supportHeights = [];
        if (context.SupportRelativeHeights.Count > 0)
        {
            AddContextSupportHeights(context, heightmap, applyOptions.FeatherWidth, supportHeights);
        }
        else
        {
            TerrainSupportStrategy strategy = SelectApplyStrategy(hasContacts);
            foreach (TerrainSupportSample sample in strategy.CollectApplySamples(zone, entries, contacts))
            {
                float targetHeight = context.BaseWorldY + sample.RelativeY - SupportFillClearance;
                if (!strategy.IsApplyTargetUsable(sample, targetHeight))
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

    public static bool TryGetWearNTearBounds(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale, out Bounds bounds)
    {
        return TryGetWearNTearWorldBounds(prefab, position, rotation, scale, out bounds);
    }

    public static bool TryGetTerrainHeight(float x, float z, out float height)
    {
        return TryGetCurrentTerrainHeight(x, z, out height);
    }

    public static bool ApplyWorldSupportContacts(IEnumerable<Vector3> supportContacts)
    {
        WorldSupportContactPlan plan = BuildWorldSupportContactPlan(supportContacts);
        if (!plan.HasSupport)
        {
            return false;
        }

        bool changed = false;
        foreach (Vector2i zone in plan.Zones)
        {
            if (!TryGetHeightmap(zone, out Heightmap heightmap))
            {
                throw new InvalidOperationException($"Target terrain zone ({zone.x},{zone.y}) is not loaded for support contact placement.");
            }

            changed |= ApplySupportCellsToHeightmap(heightmap, plan.SupportHeights, plan.SupportCells, TerrainSupportApplyOptions.SupportContacts());
        }

        return changed;
    }

    public static IEnumerator ApplyWorldSupportContactsAsync(IEnumerable<Vector3> supportContacts, Action<bool> onComplete)
    {
        WorldSupportContactPlan plan = BuildWorldSupportContactPlan(supportContacts);
        if (!plan.HasSupport)
        {
            onComplete(false);
            yield break;
        }

        bool changed = false;
        foreach (Vector2i zone in plan.Zones)
        {
            if (!TryGetHeightmap(zone, out Heightmap heightmap))
            {
                onComplete(false);
                yield break;
            }

            bool zoneChanged = false;
            yield return ApplySupportCellsToHeightmapAsync(heightmap, plan.SupportHeights, plan.SupportCells, TerrainSupportApplyOptions.SupportContacts(), result => zoneChanged = result);
            changed |= zoneChanged;
            yield return null;
        }

        onComplete(changed);
    }

    private static WorldSupportContactPlan BuildWorldSupportContactPlan(IEnumerable<Vector3> supportContacts)
    {
        List<TerrainSupportCell> supportCells = supportContacts
            .Select(contact => new TerrainSupportCell(
                Mathf.RoundToInt(contact.x),
                Mathf.RoundToInt(contact.z),
                contact.y - SupportFillClearance))
            .GroupBy(cell => PackCell(cell.X, cell.Z))
            .Select(group => group.OrderBy(cell => cell.Height).First())
            .ToList();

        Dictionary<long, float> supportHeights = supportCells.ToDictionary(cell => PackCell(cell.X, cell.Z), cell => cell.Height);
        List<Vector2i> zones = supportCells
            .Select(cell => ZoneSystem.GetZone(new Vector3(cell.X, 0f, cell.Z)))
            .Distinct()
            .ToList();

        return new WorldSupportContactPlan(supportHeights, supportCells, zones);
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

    private static bool ApplySupportCellsToHeightmap(
        Heightmap heightmap,
        Dictionary<long, float> supportHeights,
        List<TerrainSupportCell> supportCells,
        TerrainSupportApplyOptions applyOptions)
    {
        int width = heightmap.m_width + 1;
        float[] worldHeights = new float[width * width];
        Color[] paints = new Color[width * width];
        TerrainSupportCellIndex supportIndex = new(supportCells, applyOptions.FeatherWidth);
        bool changed = false;

        for (int z = 0; z < width; z++)
        {
            for (int x = 0; x < width; x++)
            {
                changed |= ComputeSupportNode(heightmap, width, x, z, supportHeights, supportIndex, applyOptions, worldHeights, paints);
            }
        }

        return changed && PersistAppliedSupportCells(heightmap, width, worldHeights, paints, applyOptions);
    }

    private static IEnumerator ApplySupportCellsToHeightmapAsync(
        Heightmap heightmap,
        Dictionary<long, float> supportHeights,
        List<TerrainSupportCell> supportCells,
        TerrainSupportApplyOptions applyOptions,
        Action<bool> onComplete)
    {
        int width = heightmap.m_width + 1;
        float[] worldHeights = new float[width * width];
        Color[] paints = new Color[width * width];
        TerrainSupportCellIndex supportIndex = new(supportCells, applyOptions.FeatherWidth);
        bool changed = false;
        int processed = 0;

        for (int z = 0; z < width; z++)
        {
            for (int x = 0; x < width; x++)
            {
                changed |= ComputeSupportNode(heightmap, width, x, z, supportHeights, supportIndex, applyOptions, worldHeights, paints);
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

        PersistAppliedSupportCells(heightmap, width, worldHeights, paints, applyOptions);
        onComplete(true);
    }

    private static bool ComputeSupportNode(
        Heightmap heightmap,
        int width,
        int x,
        int z,
        Dictionary<long, float> supportHeights,
        TerrainSupportCellIndex supportIndex,
        TerrainSupportApplyOptions applyOptions,
        float[] worldHeights,
        Color[] paints)
    {
        int index = z * width + x;
        Vector3 node = VertexToWorld(heightmap, x, z);
        float current = GetWorldHeight(heightmap, x, z);
        float baseHeight = TryGetTerrainBaseHeight(node.x, node.z, out float terrainBaseHeight) ? terrainBaseHeight : current;
        float desired = baseHeight;
        paints[index] = GetPaint(heightmap, x, z);

        if (supportHeights.TryGetValue(PackCell(Mathf.RoundToInt(node.x), Mathf.RoundToInt(node.z)), out float targetHeight))
        {
            desired = applyOptions.ClampTerrainDelta(targetHeight, baseHeight);
        }
        else if (TryGetFeatheredSupportHeight(node, baseHeight, supportIndex, applyOptions.FeatherWidth, out float featheredHeight))
        {
            desired = applyOptions.ClampTerrainDelta(featheredHeight, baseHeight);
        }

        worldHeights[index] = desired;
        return Mathf.Abs(current - desired) > 0.01f;
    }

    private static bool PersistAppliedSupportCells(
        Heightmap heightmap,
        int width,
        float[] worldHeights,
        Color[] paints,
        TerrainSupportApplyOptions applyOptions)
    {
        TerrainComp compiler = heightmap.GetAndCreateTerrainCompiler();
        PersistSupportFillTerrain(compiler, width, worldHeights, paints, applyOptions);
        heightmap.Poke(delayed: false);
        ClutterSystem.instance?.ResetGrass(heightmap.transform.position, SearchRadius);
        return true;
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

    private static Color GetPaint(Heightmap heightmap, int x, int z)
    {
        int px = Mathf.Clamp(x, 0, heightmap.m_width);
        int pz = Mathf.Clamp(z, 0, heightmap.m_width);
        return heightmap.GetPaintMask(px, pz);
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

}

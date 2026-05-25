using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static ZoneSavior.ZoneBundleTerrainGrid;

namespace ZoneSavior;

internal static partial class ZoneBundleTerrain
{
    private static List<TerrainSupportSample> CollapseToLowestSupportSamples(IEnumerable<TerrainSupportSample> samples)
    {
        Dictionary<long, TerrainSupportSample> byCell = new();
        foreach (TerrainSupportSample sample in samples)
        {
            int x = Mathf.RoundToInt(sample.WorldX);
            int z = Mathf.RoundToInt(sample.WorldZ);
            long key = PackCell(x, z);
            if (!byCell.TryGetValue(key, out TerrainSupportSample existing) || sample.RelativeY < existing.RelativeY)
            {
                byCell[key] = new TerrainSupportSample(x, z, sample.RelativeY);
            }
        }

        return byCell.Values.ToList();
    }

    private static float ResolveSupportFillBaseWorldY(List<TerrainSupportSample> samples)
    {
        if (samples.Count == 0)
        {
            return 0f;
        }

        List<float> offsets = [];
        TerrainSupportSample lowest = samples
            .OrderBy(sample => sample.RelativeY)
            .First();

        float lowestOffset = -lowest.RelativeY;
        foreach (TerrainSupportSample sample in samples)
        {
            if (!TryGetTerrainBaseHeight(sample.WorldX, sample.WorldZ, out float terrainHeight))
            {
                continue;
            }

            float offset = terrainHeight - sample.RelativeY;
            offsets.Add(offset);
            if (Mathf.Approximately(sample.WorldX, lowest.WorldX) &&
                Mathf.Approximately(sample.WorldZ, lowest.WorldZ) &&
                Mathf.Approximately(sample.RelativeY, lowest.RelativeY))
            {
                lowestOffset = offset;
            }
        }

        return offsets.Count == 0 ? lowestOffset : GetMedianOffset(offsets);
    }

    private static float ResolveFallbackSupportBaseWorldY(List<TerrainSupportSample> samples)
    {
        if (samples.Count == 0)
        {
            return 0f;
        }

        Dictionary<int, List<TerrainSupportSample>> samplesByPlane = [];
        foreach (TerrainSupportSample sample in samples)
        {
            int plane = Mathf.RoundToInt(sample.RelativeY / FallbackSupportPlaneQuantization);
            if (!samplesByPlane.TryGetValue(plane, out List<TerrainSupportSample> planeSamples))
            {
                planeSamples = [];
                samplesByPlane[plane] = planeSamples;
            }

            planeSamples.Add(sample);
        }

        KeyValuePair<int, List<TerrainSupportSample>> dominantPlane = samplesByPlane
            .OrderByDescending(item => item.Value.Count)
            .ThenBy(item => item.Key)
            .First();

        List<float> relativeHeights = dominantPlane.Value
            .Select(sample => sample.RelativeY)
            .ToList();
        float representativeRelativeY = GetMedianOffset(relativeHeights);

        List<float> terrainHeights = [];
        foreach (TerrainSupportSample sample in dominantPlane.Value)
        {
            if (TryGetTerrainBaseHeight(sample.WorldX, sample.WorldZ, out float terrainHeight))
            {
                terrainHeights.Add(terrainHeight);
            }
        }

        return terrainHeights.Count == 0
            ? -representativeRelativeY
            : GetMedianOffset(terrainHeights) - representativeRelativeY;
    }

    private static bool IsReasonableFallbackSupportTarget(TerrainSupportSample sample, float baseWorldY)
    {
        return IsReasonableFallbackTarget(sample.WorldX, sample.WorldZ, baseWorldY + sample.RelativeY - SupportFillClearance);
    }

    private static bool IsReasonableFallbackTarget(float worldX, float worldZ, float targetHeight)
    {
        return !TryGetTerrainBaseHeight(worldX, worldZ, out float nativeHeight) ||
               Mathf.Abs(targetHeight - nativeHeight) <= FallbackMaxTerrainDelta;
    }

    private static bool IsReasonableFallbackBoundsMinimum(float originY, float boundsMinY)
    {
        return boundsMinY >= originY - FallbackMaxColliderDepthBelowOrigin;
    }

    private static float GetMedianOffset(List<float> offsets)
    {
        offsets.Sort();
        int middle = offsets.Count / 2;
        return offsets.Count % 2 == 1
            ? offsets[middle]
            : (offsets[middle - 1] + offsets[middle]) * 0.5f;
    }

    private static TerrainSupportStrategy SelectPlacementStrategy(TerrainSupportTarget target)
    {
        return target.ContactsCaptured && target.Contacts.Count > 0
            ? SavedContactStrategy
            : ColliderFallbackStrategy;
    }

    private static TerrainSupportStrategy SelectApplyStrategy(bool hasContacts)
    {
        return hasContacts ? SavedContactStrategy : ColliderFallbackStrategy;
    }

    private static List<TerrainSupportSample> CollectSavedContactSamples(Vector2i zone, IEnumerable<ZoneBundleTerrainContact> contacts)
    {
        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        return contacts
            .Select(contact => new TerrainSupportSample(
                zoneCenter.x + contact.LocalX,
                zoneCenter.z + contact.LocalZ,
                contact.RelativeY))
            .ToList();
    }

    private static List<TerrainSupportSample> CollectSupportSamples(Vector2i zone, IEnumerable<ZoneBundleEntry> entries, float baseWorldY = 0f)
    {
        List<TerrainSupportSample> samples = [];
        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        bool useWorldY = !float.IsNaN(baseWorldY);

        foreach (ZoneBundleEntry entry in entries)
        {
            AddEntrySupportSamples(entry, zoneCenter, baseWorldY, useWorldY, samples);
        }

        return samples;
    }

    private static IEnumerator CollectSupportSamplesAsync(Vector2i zone, IEnumerable<ZoneBundleEntry> entries, float baseWorldY, Action<List<TerrainSupportSample>> onComplete)
    {
        List<TerrainSupportSample> samples = [];
        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        bool useWorldY = !float.IsNaN(baseWorldY);
        int processedSinceYield = 0;

        foreach (ZoneBundleEntry entry in entries)
        {
            AddEntrySupportSamples(entry, zoneCenter, baseWorldY, useWorldY, samples);

            processedSinceYield++;
            if (processedSinceYield >= TerrainContextEntryBatchSize)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        onComplete(samples);
    }

    private static void AddEntrySupportSamples(
        ZoneBundleEntry entry,
        Vector3 zoneCenter,
        float baseWorldY,
        bool useWorldY,
        List<TerrainSupportSample> samples)
    {
        GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
        if (!prefab || prefab.GetComponent<WearNTear>() == null)
        {
            return;
        }

        float y = useWorldY ? baseWorldY + entry.LocalPos[1] : entry.LocalPos[1];
        Vector3 position = new(zoneCenter.x + entry.LocalPos[0], y, zoneCenter.z + entry.LocalPos[2]);
        Quaternion rotation = new(entry.Rot[0], entry.Rot[1], entry.Rot[2], entry.Rot[3]);
        Vector3 scale = new(entry.Scale[0], entry.Scale[1], entry.Scale[2]);
        List<TerrainSupportSample> entrySamples = [];
        AddWearNTearSupportSamples(prefab, position, rotation, scale, useWorldY ? baseWorldY : 0f, entrySamples);
        float entryRelativeY = useWorldY ? position.y - baseWorldY : entry.LocalPos[1];
        float minimumReasonableY = entryRelativeY - FallbackMaxColliderDepthBelowOrigin;
        foreach (TerrainSupportSample sample in entrySamples)
        {
            if (sample.RelativeY >= minimumReasonableY)
            {
                samples.Add(sample);
            }
        }
    }

    private static void AddWearNTearSupportSamples(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale, float baseWorldY, List<TerrainSupportSample> samples)
    {
        Collider[] colliders = prefab.GetComponentsInChildren<Collider>();
        Matrix4x4 entryMatrix = Matrix4x4.TRS(position, rotation, scale);
        int before = samples.Count;

        foreach (Collider collider in colliders)
        {
            if (collider == null || !collider.enabled || collider.isTrigger || !TryGetColliderLocalBounds(collider, out Bounds colliderBounds))
            {
                continue;
            }

            Matrix4x4 colliderToRoot = prefab.transform.worldToLocalMatrix * collider.transform.localToWorldMatrix;
            Matrix4x4 colliderToWorld = entryMatrix * colliderToRoot;
            if (collider is MeshCollider { sharedMesh: not null } meshCollider &&
                AddMeshColliderSupportSamples(meshCollider.sharedMesh, colliderToWorld, baseWorldY, samples))
            {
                continue;
            }

            AddColliderBottomSamples(colliderBounds, colliderToWorld, baseWorldY, samples);
        }

        if (samples.Count == before && TryGetWearNTearWorldBounds(prefab, position, rotation, scale, out Bounds bounds))
        {
            AddBoundsSamples(bounds, samples, baseWorldY);
        }
    }

    private static void AddColliderBottomSamples(Bounds bounds, Matrix4x4 colliderToWorld, float baseWorldY, List<TerrainSupportSample> samples)
    {
        int xSteps = GetFallbackSampleSteps(bounds.size.x);
        int zSteps = GetFallbackSampleSteps(bounds.size.z);
        for (int xIndex = 0; xIndex <= xSteps; xIndex++)
        {
            float tx = xSteps == 0 ? 0.5f : xIndex / (float)xSteps;
            for (int zIndex = 0; zIndex <= zSteps; zIndex++)
            {
                float tz = zSteps == 0 ? 0.5f : zIndex / (float)zSteps;
                Vector3 local = new(
                    Mathf.Lerp(bounds.min.x, bounds.max.x, tx),
                    bounds.min.y,
                    Mathf.Lerp(bounds.min.z, bounds.max.z, tz));
                Vector3 world = colliderToWorld.MultiplyPoint3x4(local);
                samples.Add(new TerrainSupportSample(world.x, world.z, world.y - baseWorldY));
            }
        }
    }

    private static bool AddMeshColliderSupportSamples(Mesh mesh, Matrix4x4 colliderToWorld, float baseWorldY, List<TerrainSupportSample> samples)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        int before = samples.Count;
        for (int index = 0; index + 2 < triangles.Length; index += 3)
        {
            Vector3 a = colliderToWorld.MultiplyPoint3x4(vertices[triangles[index]]);
            Vector3 b = colliderToWorld.MultiplyPoint3x4(vertices[triangles[index + 1]]);
            Vector3 c = colliderToWorld.MultiplyPoint3x4(vertices[triangles[index + 2]]);
            AddTriangleSupportSamples(a, b, c, baseWorldY, samples);
        }

        return samples.Count > before;
    }

    private static void AddTriangleSupportSamples(Vector3 a, Vector3 b, Vector3 c, float baseWorldY, List<TerrainSupportSample> samples)
    {
        float minX = Mathf.Floor(Mathf.Min(a.x, Mathf.Min(b.x, c.x)));
        float maxX = Mathf.Ceil(Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
        float minZ = Mathf.Floor(Mathf.Min(a.z, Mathf.Min(b.z, c.z)));
        float maxZ = Mathf.Ceil(Mathf.Max(a.z, Mathf.Max(b.z, c.z)));
        float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
        if (Mathf.Abs(denominator) < 0.0001f)
        {
            return;
        }

        for (float x = minX; x <= maxX; x += FallbackColliderSampleStep)
        {
            for (float z = minZ; z <= maxZ; z += FallbackColliderSampleStep)
            {
                if (TryGetTriangleYAtXZ(a, b, c, denominator, x, z, out float y))
                {
                    samples.Add(new TerrainSupportSample(x, z, y - baseWorldY));
                }
            }
        }
    }

    private static bool TryGetTriangleYAtXZ(Vector3 a, Vector3 b, Vector3 c, float denominator, float x, float z, out float y)
    {
        y = 0f;
        float wa = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / denominator;
        float wb = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / denominator;
        float wc = 1f - wa - wb;
        const float epsilon = -0.001f;
        if (wa < epsilon || wb < epsilon || wc < epsilon)
        {
            return false;
        }

        y = wa * a.y + wb * b.y + wc * c.y;
        return true;
    }

    private static int GetFallbackSampleSteps(float size)
    {
        return Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(size) / FallbackColliderSampleStep), 1, 64);
    }

    private static void AddBoundsSamples(Bounds bounds, List<TerrainSupportSample> samples, float baseWorldY)
    {
        int minX = Mathf.FloorToInt(bounds.min.x);
        int maxX = Mathf.CeilToInt(bounds.max.x);
        int minZ = Mathf.FloorToInt(bounds.min.z);
        int maxZ = Mathf.CeilToInt(bounds.max.z);

        if (minX == maxX)
        {
            maxX++;
        }

        if (minZ == maxZ)
        {
            maxZ++;
        }

        float relativeY = float.IsNaN(baseWorldY) ? bounds.min.y : bounds.min.y - baseWorldY;
        for (float x = minX; x <= maxX; x += SupportFillSampleStep)
        {
            for (float z = minZ; z <= maxZ; z += SupportFillSampleStep)
            {
                samples.Add(new TerrainSupportSample(x, z, relativeY));
            }
        }
    }

    private static bool TryReadSupportWearNTear(ZDO zdo, Vector2i zone, ZoneBundleWearNTearSaveMode saveMode, out GameObject prefab)
    {
        prefab = null!;
        if (zdo == null || !zdo.IsValid() || ZoneSystem.GetZone(zdo.GetPosition()) != zone)
        {
            return false;
        }

        if (saveMode == ZoneBundleWearNTearSaveMode.CreatorOnly && zdo.GetLong(ZDOVars.s_creator, 0L) == 0L)
        {
            return false;
        }

        prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        return prefab && prefab.GetComponent<WearNTear>() != null;
    }

    private static bool TryReadTamedMonster(ZDO zdo, Vector2i zone, out GameObject prefab)
    {
        prefab = null!;
        if (zdo == null || !zdo.IsValid() || ZoneSystem.GetZone(zdo.GetPosition()) != zone || !zdo.GetBool(ZDOVars.s_tamed, false))
        {
            return false;
        }

        prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        return prefab && prefab.GetComponent<Tameable>() != null && prefab.GetComponent<MonsterAI>() != null;
    }

    private static bool TryGetWearNTearWorldBounds(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale, out Bounds bounds)
    {
        bounds = default;
        Collider[] colliders = prefab.GetComponentsInChildren<Collider>();
        Matrix4x4 entryMatrix = Matrix4x4.TRS(position, rotation, scale);
        bool initialized = false;

        foreach (Collider collider in colliders)
        {
            if (collider == null || !collider.enabled || collider.isTrigger || !TryGetColliderLocalBounds(collider, out Bounds colliderBounds))
            {
                continue;
            }

            Matrix4x4 colliderToRoot = prefab.transform.worldToLocalMatrix * collider.transform.localToWorldMatrix;
            foreach (Vector3 corner in GetBoundsCorners(colliderBounds))
            {
                Vector3 world = entryMatrix.MultiplyPoint3x4(colliderToRoot.MultiplyPoint3x4(corner));
                if (initialized)
                {
                    bounds.Encapsulate(world);
                }
                else
                {
                    bounds = new Bounds(world, Vector3.zero);
                    initialized = true;
                }
            }
        }

        return initialized;
    }

    private static bool TryGetColliderLocalBounds(Collider collider, out Bounds bounds)
    {
        switch (collider)
        {
            case BoxCollider box:
                bounds = new Bounds(box.center, box.size);
                return true;
            case SphereCollider sphere:
                bounds = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
                return true;
            case CapsuleCollider capsule:
                Vector3 size = Vector3.one * (capsule.radius * 2f);
                size[capsule.direction] = Mathf.Max(capsule.height, capsule.radius * 2f);
                bounds = new Bounds(capsule.center, size);
                return true;
            case MeshCollider meshCollider when meshCollider.sharedMesh != null:
                bounds = meshCollider.sharedMesh.bounds;
                return true;
            default:
                Bounds worldBounds = collider.bounds;
                bounds = new Bounds(collider.transform.InverseTransformPoint(worldBounds.center), worldBounds.size);
                return true;
        }
    }

    private static IEnumerable<Vector3> GetBoundsCorners(Bounds bounds)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;
        yield return new Vector3(min.x, min.y, min.z);
        yield return new Vector3(min.x, min.y, max.z);
        yield return new Vector3(min.x, max.y, min.z);
        yield return new Vector3(min.x, max.y, max.z);
        yield return new Vector3(max.x, min.y, min.z);
        yield return new Vector3(max.x, min.y, max.z);
        yield return new Vector3(max.x, max.y, min.z);
        yield return new Vector3(max.x, max.y, max.z);
    }

    private static Vector3 ReadScale(ZDO zdo, GameObject prefab)
    {
        return zdo.GetVec3(ZDOVars.s_scaleHash, prefab.transform.localScale);
    }

    private abstract class TerrainSupportStrategy
    {
        public abstract List<TerrainSupportSample> CollectPlacementSamples(TerrainSupportTarget target);

        public abstract List<TerrainSupportSample> CollectApplySamples(
            Vector2i zone,
            IEnumerable<ZoneBundleEntry> entries,
            IReadOnlyCollection<ZoneBundleTerrainContact> contacts);

        public abstract float ResolveBaseWorldY(List<TerrainSupportSample> footprintSamples);

        public virtual bool IsPlacementTargetUsable(TerrainSupportSample sample, float baseWorldY)
        {
            return true;
        }

        public virtual bool IsApplyTargetUsable(TerrainSupportSample sample, float targetHeight)
        {
            return true;
        }
    }

    private sealed class SavedContactTerrainStrategy : TerrainSupportStrategy
    {
        public override List<TerrainSupportSample> CollectPlacementSamples(TerrainSupportTarget target)
        {
            return CollectSavedContactSamples(target.Zone, target.Contacts);
        }

        public override List<TerrainSupportSample> CollectApplySamples(
            Vector2i zone,
            IEnumerable<ZoneBundleEntry> entries,
            IReadOnlyCollection<ZoneBundleTerrainContact> contacts)
        {
            return CollectSavedContactSamples(zone, contacts);
        }

        public override float ResolveBaseWorldY(List<TerrainSupportSample> footprintSamples)
        {
            return ResolveSupportFillBaseWorldY(footprintSamples);
        }
    }

    private sealed class ColliderFallbackTerrainStrategy : TerrainSupportStrategy
    {
        public override List<TerrainSupportSample> CollectPlacementSamples(TerrainSupportTarget target)
        {
            return CollectSupportSamples(target.Zone, target.Entries, target.SourceBaseY);
        }

        public override List<TerrainSupportSample> CollectApplySamples(
            Vector2i zone,
            IEnumerable<ZoneBundleEntry> entries,
            IReadOnlyCollection<ZoneBundleTerrainContact> contacts)
        {
            return CollectSupportSamples(zone, entries);
        }

        public override float ResolveBaseWorldY(List<TerrainSupportSample> footprintSamples)
        {
            return ResolveFallbackSupportBaseWorldY(footprintSamples);
        }

        public override bool IsPlacementTargetUsable(TerrainSupportSample sample, float baseWorldY)
        {
            return IsReasonableFallbackSupportTarget(sample, baseWorldY);
        }

        public override bool IsApplyTargetUsable(TerrainSupportSample sample, float targetHeight)
        {
            return IsReasonableFallbackTarget(sample.WorldX, sample.WorldZ, targetHeight);
        }
    }

    private readonly struct PlacementSupportSampleSet
    {
        public PlacementSupportSampleSet(TerrainSupportStrategy strategy, List<TerrainSupportSample> samples)
        {
            Strategy = strategy;
            Samples = samples;
        }

        public TerrainSupportStrategy Strategy { get; }
        public List<TerrainSupportSample> Samples { get; }
    }

    private readonly struct TerrainSupportSample
    {
        public TerrainSupportSample(float worldX, float worldZ, float relativeY)
        {
            WorldX = worldX;
            WorldZ = worldZ;
            RelativeY = relativeY;
        }

        public float WorldX { get; }
        public float WorldZ { get; }
        public float RelativeY { get; }
    }
}

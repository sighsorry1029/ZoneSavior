using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneTerrainContactSampler
{
    public static List<TerrainWorldContact> CaptureWorldContacts(IEnumerable<TerrainContactSource> sources, float tolerance)
    {
        Dictionary<long, TerrainWorldContact> lowestByCell = [];
        foreach (TerrainContactSource source in sources)
        {
            if (!source.Prefab || source.Prefab.GetComponent<WearNTear>() == null)
            {
                continue;
            }

            if (!ZoneBundleTerrain.TryGetWearNTearBounds(source.Prefab, source.Position, source.Rotation, source.Scale, out Bounds bounds))
            {
                continue;
            }

            AddLowestBoundsFootprintContacts(bounds, lowestByCell);
        }

        List<TerrainWorldContact> contacts = [];
        foreach (TerrainWorldContact candidate in lowestByCell.Values.OrderBy(contact => contact.WorldZ).ThenBy(contact => contact.WorldX))
        {
            if (!ZoneBundleTerrain.TryGetTerrainHeight(candidate.WorldX, candidate.WorldZ, out float terrainY) ||
                Mathf.Abs(terrainY - candidate.WorldY) > tolerance)
            {
                continue;
            }

            contacts.Add(candidate);
        }

        return contacts;
    }

    public static List<TerrainContactSource> FromZoneEntries(Vector2i zone, float sourceBaseY, IEnumerable<ZoneBundleEntry> entries)
    {
        List<TerrainContactSource> sources = [];
        if (float.IsNaN(sourceBaseY) || ZNetScene.instance == null)
        {
            return sources;
        }

        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        foreach (ZoneBundleEntry entry in entries)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            if (!prefab || prefab.GetComponent<WearNTear>() == null)
            {
                continue;
            }

            sources.Add(new TerrainContactSource(
                prefab,
                new Vector3(zoneCenter.x + entry.LocalPos[0], sourceBaseY + entry.LocalPos[1], zoneCenter.z + entry.LocalPos[2]),
                new Quaternion(entry.Rot[0], entry.Rot[1], entry.Rot[2], entry.Rot[3]),
                new Vector3(entry.Scale[0], entry.Scale[1], entry.Scale[2])));
        }

        return sources;
    }

    public static List<ZoneBundleTerrainContact> ToZoneBundleContacts(Vector2i zone, float sourceBaseY, IEnumerable<TerrainWorldContact> contacts)
    {
        Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
        return contacts
            .Select(contact => new ZoneBundleTerrainContact
            {
                LocalX = Round(contact.WorldX - zoneCenter.x),
                LocalZ = Round(contact.WorldZ - zoneCenter.z),
                RelativeY = Round(contact.WorldY - sourceBaseY)
            })
            .OrderBy(contact => contact.LocalZ)
            .ThenBy(contact => contact.LocalX)
            .ToList();
    }

    private static void AddLowestBoundsFootprintContacts(Bounds bounds, Dictionary<long, TerrainWorldContact> lowestByCell)
    {
        float bottomY = bounds.min.y;
        int minX = Mathf.FloorToInt(bounds.min.x);
        int maxX = Mathf.CeilToInt(bounds.max.x);
        int minZ = Mathf.FloorToInt(bounds.min.z);
        int maxZ = Mathf.CeilToInt(bounds.max.z);

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                long key = PackCell(x, z);
                TerrainWorldContact contact = new(x, z, bottomY);
                if (!lowestByCell.TryGetValue(key, out TerrainWorldContact existing) || contact.WorldY < existing.WorldY)
                {
                    lowestByCell[key] = contact;
                }
            }
        }
    }

    private static long PackCell(int x, int z)
    {
        return ((long)x << 32) ^ (uint)z;
    }

    private static float Round(float value)
    {
        return Mathf.Round(value * 1000f) / 1000f;
    }
}

internal readonly struct TerrainContactSource
{
    public TerrainContactSource(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Prefab = prefab;
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }

    public GameObject Prefab { get; }
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public Vector3 Scale { get; }
}

internal readonly struct TerrainWorldContact
{
    public TerrainWorldContact(int cellX, int cellZ, float worldY)
    {
        WorldX = cellX;
        WorldZ = cellZ;
        WorldY = worldY;
    }

    public float WorldX { get; }
    public float WorldZ { get; }
    public float WorldY { get; }

    public Vector3 ToVector3()
    {
        return new Vector3(WorldX, WorldY, WorldZ);
    }
}


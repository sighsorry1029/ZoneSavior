using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneTerrainContactSampler
{
    public static List<TerrainWorldContact> CaptureWorldContacts(IEnumerable<TerrainContactSource> sources, float tolerance)
    {
        Dictionary<long, TerrainWorldContact> lowestContactByCell = [];
        Dictionary<long, float> terrainHeightByCell = [];
        foreach (TerrainContactSource source in sources)
        {
            if (!source.Prefab || source.Prefab.GetComponent<WearNTear>() == null)
            {
                continue;
            }

            foreach (TerrainWorldContact candidate in ZoneBundleTerrain.CollectWearNTearWorldSupportCandidates(
                         source.Prefab,
                         source.Position,
                         source.Rotation,
                         source.Scale))
            {
                long key = PackCell((int)candidate.WorldX, (int)candidate.WorldZ);
                if (!terrainHeightByCell.TryGetValue(key, out float terrainY))
                {
                    terrainY = ZoneBundleTerrain.TryGetTerrainHeight(candidate.WorldX, candidate.WorldZ, out float height)
                        ? height
                        : float.NaN;
                    terrainHeightByCell[key] = terrainY;
                }

                if (float.IsNaN(terrainY) || Mathf.Abs(terrainY - candidate.WorldY) > tolerance)
                {
                    continue;
                }

                if (!lowestContactByCell.TryGetValue(key, out TerrainWorldContact existing) || candidate.WorldY < existing.WorldY)
                {
                    lowestContactByCell[key] = candidate;
                }
            }
        }

        return lowestContactByCell.Values
            .OrderBy(contact => contact.WorldZ)
            .ThenBy(contact => contact.WorldX)
            .ToList();
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

}


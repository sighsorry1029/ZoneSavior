using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

// Valheim 1.0.7 private contracts. Resolve once against original game assemblies;
// no publicized compile references or per-frame member searches are required.
internal static class ZoneSaviorGameAccess
{
    internal static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> Sectors =
        AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
    internal static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> Instances =
        AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
    internal static readonly AccessTools.FieldRef<TextInput, bool> TextInputVisible =
        AccessTools.FieldRefAccess<TextInput, bool>("m_visibleFrame");
    internal static readonly AccessTools.FieldRef<Heightmap, HeightmapBuilder.HMBuildData> BuildData =
        AccessTools.FieldRefAccess<Heightmap, HeightmapBuilder.HMBuildData>("m_buildData");
    internal static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedZones =
        AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");
    private static readonly AccessTools.FieldRef<ZoneSystem, IDictionary> LoadedZones =
        AccessTools.FieldRefAccess<ZoneSystem, IDictionary>("m_zones");
    private static readonly FieldInfo ZoneRoot = AccessTools.Field(
        AccessTools.Inner(typeof(ZoneSystem), "ZoneData"), "m_root");

    internal static readonly Action<ZDOMan, ZDOID> HandleDestroyedZdo =
        AccessTools.MethodDelegate<Action<ZDOMan, ZDOID>>(AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO", new[] { typeof(ZDOID) }));
    internal static readonly Action<ZDOMan> SendDestroyed =
        AccessTools.MethodDelegate<Action<ZDOMan>>(AccessTools.Method(typeof(ZDOMan), "SendDestroyed", Type.EmptyTypes));
    internal static readonly Func<ZNetScene, ZDO, GameObject> CreateObject =
        AccessTools.MethodDelegate<Func<ZNetScene, ZDO, GameObject>>(AccessTools.Method(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) }));
    internal static readonly Action<Minimap, float> UpdateLocationPins =
        AccessTools.MethodDelegate<Action<Minimap, float>>(AccessTools.Method(typeof(Minimap), "UpdateLocationPins", new[] { typeof(float) }));

    internal static void UnloadZone(ZoneSystem system, Vector2s zone)
    {
        GeneratedZones(system).Remove(zone);
        IDictionary zones = LoadedZones(system);
        if (zones.Contains(zone))
        {
            UnityEngine.Object.Destroy((GameObject)ZoneRoot.GetValue(zones[zone]));
            zones.Remove(zone);
        }
    }
}

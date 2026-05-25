using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal readonly struct TerrainSupportApplyOptions
{
    private TerrainSupportApplyOptions(float featherWidth, float maxTerrainDelta, bool useVanillaTerrainDelta)
    {
        FeatherWidth = featherWidth;
        MaxTerrainDelta = maxTerrainDelta;
        UseVanillaTerrainDelta = useVanillaTerrainDelta;
    }

    public float FeatherWidth { get; }
    public float MaxTerrainDelta { get; }
    public bool UseVanillaTerrainDelta { get; }

    public static TerrainSupportApplyOptions ZoneBundle()
    {
        return new TerrainSupportApplyOptions(
            ZoneBundleConfig.SupportFillFeatherWidth,
            8f,
            useVanillaTerrainDelta: true);
    }

    public static TerrainSupportApplyOptions SupportContacts()
    {
        return new TerrainSupportApplyOptions(
            ZoneBundleConfig.SupportFillFeatherWidth,
            0f,
            useVanillaTerrainDelta: false);
    }

    public float ClampTerrainDelta(float desired, float nativeHeight)
    {
        return MaxTerrainDelta <= 0f
            ? desired
            : Mathf.Clamp(desired, nativeHeight - MaxTerrainDelta, nativeHeight + MaxTerrainDelta);
    }
}

internal enum ZoneBundleWearNTearSaveMode
{
    CreatorOnly = 0,
    IncludeCreatorless = 1
}

[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.ApplyModifiers))]
internal static class ZoneBundleTerrainBaseLayerPatch
{
    private static void Prefix(Heightmap __instance)
    {
        ZoneBundleTerrain.ApplyBaseLayer(__instance);
    }
}

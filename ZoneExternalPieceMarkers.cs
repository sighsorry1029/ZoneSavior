using UnityEngine;

namespace ZoneSavior;

internal static class ZoneExternalPieceMarkers
{
    private static readonly int HomesteadBlueprintPieceHash =
        StringExtensionMethods.GetStableHashCode("sighsorry.Homestead.blueprint_piece");

    public static bool ShouldForceCount(ZDO zdo)
    {
        return zdo.GetBool(HomesteadBlueprintPieceHash, false);
    }
}

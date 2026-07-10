using System.Collections.Generic;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneStructureClassifier
{
    public static bool TryGetAutoArchiveCandidate(
        ZDO zdo,
        out Vector2i objectZone,
        out long creatorPlayerId,
        out string creatorName)
    {
        objectZone = default;
        creatorPlayerId = 0L;
        creatorName = "";
        if (zdo == null || !zdo.IsValid())
        {
            return false;
        }

        GameObject? prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
        creatorPlayerId = zdo.GetLong(ZDOVars.s_creator, 0L);
        if (!prefab ||
            creatorPlayerId == 0L ||
            prefab.GetComponent<WearNTear>() == null ||
            !ZoneSaviorBuildRecipeRules.HasBuildRecipe(prefab))
        {
            creatorPlayerId = 0L;
            return false;
        }

        objectZone = ZoneSystem.GetZone(zdo.GetPosition());
        creatorName = zdo.GetString(ZDOVars.s_creatorName, "");
        return true;
    }

    public static ZoneStructureInfo Inspect(ZDO zdo, Vector2i? requestedZone = null)
    {
        ZoneStructureInfo info = new();
        if (zdo == null || !zdo.IsValid())
        {
            info.ExclusionReasons.Add("invalid_zdo");
            return info;
        }

        Vector3 position = zdo.GetPosition();
        Vector2i objectZone = ZoneSystem.GetZone(position);
        GameObject? prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
        bool hasPrefab = prefab != null && prefab;
        bool inRequestedZone = requestedZone == null || objectZone == requestedZone.Value;

        info.ZdoId = zdo.m_uid.ToString();
        info.PrefabHash = zdo.GetPrefab();
        info.Prefab = hasPrefab ? Utils.GetPrefabName(prefab!) : "";
        info.Position = position;
        info.ObjectZone = objectZone;
        info.CreatorPlayerId = zdo.GetLong(ZDOVars.s_creator, 0L);
        info.CreatorName = zdo.GetString(ZDOVars.s_creatorName, "");
        info.HasPrefab = hasPrefab;
        info.HasZNetView = hasPrefab && prefab!.GetComponent<ZNetView>() != null;
        info.HasWearNTear = hasPrefab && prefab!.GetComponent<WearNTear>() != null;
        info.HasPiece = hasPrefab && prefab!.GetComponent<Piece>() != null;
        info.HasBuildRecipe = hasPrefab && ZoneSaviorBuildRecipeRules.HasBuildRecipe(prefab!);
        info.InRequestedZone = inRequestedZone;

        if (!inRequestedZone)
        {
            info.ExclusionReasons.Add("outside_requested_zone");
        }

        if (!hasPrefab)
        {
            info.ExclusionReasons.Add("prefab_missing");
        }

        if (hasPrefab && !info.HasZNetView)
        {
            info.ExclusionReasons.Add("no_znetview");
        }

        if (!info.HasWearNTear)
        {
            info.ExclusionReasons.Add("not_wearntear");
        }

        if (info.CreatorPlayerId == 0L)
        {
            info.ExclusionReasons.Add("creatorless_or_missing_creator");
        }

        if (info.HasWearNTear && !info.HasBuildRecipe)
        {
            info.ExclusionReasons.Add("no_player_build_recipe_or_resource_cost");
        }

        info.AutoArchiveCandidatePiece = inRequestedZone &&
                                         info.CreatorPlayerId != 0L &&
                                         info.HasWearNTear &&
                                         info.HasBuildRecipe;
        if (info.AutoArchiveCandidatePiece)
        {
            info.ExclusionReasons.Clear();
        }

        return info;
    }
}

internal sealed class ZoneStructureInfo
{
    public string ZdoId { get; set; } = "";
    public int PrefabHash { get; set; }
    public string Prefab { get; set; } = "";
    public Vector3 Position { get; set; }
    public Vector2i ObjectZone { get; set; }
    public long CreatorPlayerId { get; set; }
    public string CreatorName { get; set; } = "";
    public bool HasPrefab { get; set; }
    public bool HasZNetView { get; set; }
    public bool HasWearNTear { get; set; }
    public bool HasPiece { get; set; }
    public bool HasBuildRecipe { get; set; }
    public bool InRequestedZone { get; set; }
    public bool AutoArchiveCandidatePiece { get; set; }
    public List<string> ExclusionReasons { get; } = [];
}

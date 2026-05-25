using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneSaviorBuildRecipeRules
{
    private static readonly Dictionary<string, bool> BuildRecipeCache = new(StringComparer.Ordinal);
    private static int _buildRecipeCacheObjectDbCount = -1;

    internal static bool HasBuildRecipe(GameObject? prefab)
    {
        if (!prefab)
        {
            return false;
        }

        Piece piece = prefab.GetComponent<Piece>();
        if (piece == null || piece.m_resources == null)
        {
            return false;
        }

        return HasResourceCost(piece) && IsRegisteredPlayerBuildPiece(prefab);
    }

    private static bool HasResourceCost(Piece piece)
    {
        return piece.m_resources.Any(requirement => requirement.m_resItem && requirement.m_amount > 0);
    }

    private static bool IsRegisteredPlayerBuildPiece(GameObject prefab)
    {
        string prefabName = Utils.GetPrefabName(prefab);
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return false;
        }

        ObjectDB? objectDb = ObjectDB.instance;
        if (objectDb == null || objectDb.m_items == null)
        {
            return false;
        }

        if (_buildRecipeCacheObjectDbCount != objectDb.m_items.Count)
        {
            BuildRecipeCache.Clear();
            _buildRecipeCacheObjectDbCount = objectDb.m_items.Count;
        }

        if (BuildRecipeCache.TryGetValue(prefabName, out bool cached))
        {
            return cached;
        }

        foreach (GameObject itemPrefab in objectDb.m_items)
        {
            ItemDrop itemDrop = itemPrefab ? itemPrefab.GetComponent<ItemDrop>() : null!;
            PieceTable? pieceTable = itemDrop?.m_itemData.m_shared.m_buildPieces;
            if (pieceTable?.m_pieces == null)
            {
                continue;
            }

            if (pieceTable.m_pieces.Any(piecePrefab =>
                    piecePrefab &&
                    string.Equals(Utils.GetPrefabName(piecePrefab), prefabName, StringComparison.Ordinal)))
            {
                BuildRecipeCache[prefabName] = true;
                return true;
            }
        }

        BuildRecipeCache[prefabName] = false;
        return false;
    }
}

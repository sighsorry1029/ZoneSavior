using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ZoneSavior;

internal static class ZoneSaviorBuildRecipeRules
{
    private static readonly HashSet<string> BuildRecipePrefabs = new(StringComparer.Ordinal);
    private static ObjectDB? _indexedObjectDb;
    private static int _indexedItemCount = -1;

    internal static void RefreshIndex()
    {
        BuildRecipePrefabs.Clear();
        _indexedObjectDb = null;
        _indexedItemCount = -1;
        if (ObjectDB.instance != null && ObjectDB.instance.m_items != null)
        {
            EnsureIndex(ObjectDB.instance);
        }
    }

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

        EnsureIndex(objectDb);
        return BuildRecipePrefabs.Contains(prefabName);
    }

    private static void EnsureIndex(ObjectDB objectDb)
    {
        if (ReferenceEquals(_indexedObjectDb, objectDb) && _indexedItemCount == objectDb.m_items.Count)
        {
            return;
        }

        BuildRecipePrefabs.Clear();
        foreach (GameObject itemPrefab in objectDb.m_items)
        {
            ItemDrop itemDrop = itemPrefab ? itemPrefab.GetComponent<ItemDrop>() : null!;
            PieceTable? pieceTable = itemDrop?.m_itemData.m_shared.m_buildPieces;
            if (pieceTable?.m_pieces == null)
            {
                continue;
            }

            foreach (GameObject piecePrefab in pieceTable.m_pieces)
            {
                if (!piecePrefab)
                {
                    continue;
                }

                string name = Utils.GetPrefabName(piecePrefab);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    BuildRecipePrefabs.Add(name);
                }
            }
        }

        _indexedObjectDb = objectDb;
        _indexedItemCount = objectDb.m_items.Count;
    }
}

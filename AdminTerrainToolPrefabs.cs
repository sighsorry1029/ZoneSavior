using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    public static void AddToAvailablePieces(PieceTable table, Player player)
    {
        if (!ShouldShowTo(player))
        {
            return;
        }

        EnsureRegistered();
        if (!_prefab || !_slopePrefab || !_paintPrefab || !_resetPrefab || !_paintResetPrefab)
        {
            return;
        }

        AddPieceToTable(table, _prefab);
        AddPieceToTable(table, _slopePrefab);
        AddPieceToTable(table, _paintPrefab);
        AddPieceToTable(table, _paintResetPrefab);
        AddPieceToTable(table, _resetPrefab);
    }

    private static GameObject? EnsureRegistered()
    {
        ZNetScene scene = ZNetScene.instance;
        if (scene == null)
        {
            return _prefab;
        }

        if (!_prefab)
        {
            _prefab = CreatePrefab(PrefabName, slope: false);
        }

        if (!_slopePrefab)
        {
            _slopePrefab = CreatePrefab(SlopePrefabName, slope: true);
        }

        if (!_paintPrefab)
        {
            _paintPrefab = CreatePrefab(PaintPrefabName, slope: false, paint: true);
        }

        if (!_resetPrefab)
        {
            _resetPrefab = CreatePrefab(ResetPrefabName, slope: false, reset: true);
        }

        if (!_paintResetPrefab)
        {
            _paintResetPrefab = CreatePrefab(PaintResetPrefabName, slope: false, paintReset: true);
        }

        if (_registeredScene == scene &&
            IsRegistered(scene, PrefabHash) &&
            IsRegistered(scene, SlopePrefabHash) &&
            IsRegistered(scene, PaintPrefabHash) &&
            IsRegistered(scene, ResetPrefabHash) &&
            IsRegistered(scene, PaintResetPrefabHash))
        {
            return _prefab;
        }

        RegisterPrefab(scene, PrefabHash, PrefabName, _prefab);
        RegisterPrefab(scene, SlopePrefabHash, SlopePrefabName, _slopePrefab);
        RegisterPrefab(scene, PaintPrefabHash, PaintPrefabName, _paintPrefab);
        RegisterPrefab(scene, PaintResetPrefabHash, PaintResetPrefabName, _paintResetPrefab);
        RegisterPrefab(scene, ResetPrefabHash, ResetPrefabName, _resetPrefab);
        _registeredScene = scene;

        return _prefab;
    }

    private static bool IsRegistered(ZNetScene scene, int prefabHash)
    {
        return scene.m_namedPrefabs.TryGetValue(prefabHash, out GameObject prefab) && prefab;
    }

    private static GameObject? GetRegisteredPrefab(int prefabHash)
    {
        EnsureRegistered();
        return prefabHash == PrefabHash ? _prefab :
            prefabHash == SlopePrefabHash ? _slopePrefab :
            prefabHash == PaintPrefabHash ? _paintPrefab :
            prefabHash == ResetPrefabHash ? _resetPrefab :
            prefabHash == PaintResetPrefabHash ? _paintResetPrefab :
            null;
    }

    private static void RegisterPrefab(ZNetScene scene, int prefabHash, string prefabName, GameObject prefab)
    {
        if (!scene.m_namedPrefabs.ContainsKey(prefabHash))
        {
            scene.m_namedPrefabs[prefabHash] = prefab;
        }

        if (!scene.m_prefabs.Any(registered => registered && string.Equals(Utils.GetPrefabName(registered), prefabName, StringComparison.Ordinal)))
        {
            scene.m_prefabs.Add(prefab);
        }
    }

    private static void AddPieceToTable(PieceTable table, GameObject prefab)
    {
        string prefabName = Utils.GetPrefabName(prefab);
        if (!table.m_pieces.Any(piece => piece && string.Equals(Utils.GetPrefabName(piece), prefabName, StringComparison.Ordinal)))
        {
            table.m_pieces.Add(prefab);
        }

        Piece pieceComponent = prefab.GetComponent<Piece>();
        pieceComponent.m_category = Piece.PieceCategory.Misc;
        pieceComponent.m_clipEverything = true;
        pieceComponent.m_icon = GetIcon(IsSlopeProxyObject(prefab), IsResetProxyObject(prefab), IsPaintProxyObject(prefab), IsPaintResetProxyObject(prefab));
        pieceComponent.m_description = GetPieceDescription(prefab);
        int category = Mathf.Clamp((int)pieceComponent.m_category, 0, table.m_availablePieces.Count - 1);
        if (category < 0)
        {
            return;
        }

        List<Piece> pieces = table.m_availablePieces[category];
        if (!pieces.Any(piece => piece && string.Equals(Utils.GetPrefabName(piece.gameObject), prefabName, StringComparison.Ordinal)))
        {
            pieces.Add(pieceComponent);
        }
    }

    private static void UpdatePieceDescriptions()
    {
        UpdatePieceDescription(_prefab);
        UpdatePieceDescription(_slopePrefab);
        UpdatePieceDescription(_paintPrefab);
        UpdatePieceDescription(_paintResetPrefab);
        UpdatePieceDescription(_resetPrefab);
    }

    private static void UpdatePieceDescription(GameObject? prefab)
    {
        if (!prefab)
        {
            return;
        }

        Piece piece = prefab.GetComponent<Piece>();
        if (piece)
        {
            piece.m_description = GetPieceDescription(prefab);
        }
    }

    private static string GetPieceDescription(GameObject prefab)
    {
        if (IsSlopeProxyObject(prefab))
        {
            return string.Join("\n",
                "Places start and end markers to store and replay a terrain slope.",
                "",
                $"Width: {FormatMeters(CurrentSlopeWidth())}",
                $"Start marker: {(_hasPendingSlopeStart ? "set" : "not set")}",
                FormatTerrainToolWheelAction("width"));
        }

        if (IsPaintProxyObject(prefab))
        {
            return string.Join("\n",
                "Stores and replays terrain paint in this circle.",
                "",
                $"Current radius: {FormatMeters(CurrentCircleRadius())}",
                $"Paint: {CurrentPaintType()}",
                FormatTerrainToolWheelAction("paint type"));
        }

        if (IsPaintResetProxyObject(prefab))
        {
            return string.Join("\n",
                "Resets terrain paint in this radius and removes intersecting ZoneSavior paint proxies.",
                "",
                $"Current radius: {FormatMeters(CurrentCircleRadius())}");
        }

        if (IsResetProxyObject(prefab))
        {
            return string.Join("\n",
                "Resets terrain in this radius and removes intersecting ZoneSavior terrain proxies.",
                "",
                $"Current radius: {FormatMeters(CurrentCircleRadius())}");
        }

        return string.Join("\n",
            "Stores and replays terrain filled to this marker height.",
            "",
            $"Current radius: {FormatMeters(CurrentCircleRadius())}",
            $"Edge Softness: {CurrentTerrainEdgeSoftness():0.##}",
            FormatTerrainToolWheelAction("edge softness"));
    }

    private static string FormatMeters(float value)
    {
        return $"{value:0.#}m";
    }

    private static string FormatTerrainToolWheelAction(string action)
    {
        string modifier = FormatShortcut(AdminTerrainToolConfig.TerrainToolModifierKey);
        return string.IsNullOrWhiteSpace(modifier)
            ? $"Wheel: {action}"
            : $"{modifier} + wheel: {action}";
    }

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        List<string> keys = shortcut.Modifiers
            .Select(FormatKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToList();

        string mainKey = FormatKey(shortcut.MainKey);
        if (!string.IsNullOrWhiteSpace(mainKey))
        {
            keys.Add(mainKey);
        }

        return keys.Count > 0 ? string.Join(" + ", keys) : "";
    }

    private static string FormatKey(KeyCode key)
    {
        return key switch
        {
            KeyCode.None => "",
            KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
            KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
            KeyCode.LeftShift or KeyCode.RightShift => "Shift",
            _ => key.ToString()
        };
    }

    private static GameObject CreatePrefab(string prefabName, bool slope, bool reset = false, bool paint = false, bool paintReset = false)
    {
        GameObject prefab = new(prefabName);
        prefab.name = prefabName;
        prefab.SetActive(false);

        int pieceLayer = LayerMask.NameToLayer("piece");
        if (pieceLayer >= 0)
        {
            prefab.layer = pieceLayer;
        }

        ZNetView nview = prefab.AddComponent<ZNetView>();
        nview.m_persistent = !(reset || paintReset);
        nview.m_distant = false;
        nview.m_type = ZDO.ObjectType.Solid;
        nview.m_syncInitialScale = true;

        Piece piece = prefab.AddComponent<Piece>();
        piece.m_name = paintReset
            ? "ZoneSavior Paint Reset"
            : paint
            ? "ZoneSavior Paint Proxy"
            : reset
            ? "ZoneSavior Terrain Reset"
            : slope
                ? "ZoneSavior TerrainProxy Slope"
                : "ZoneSavior Terrain Proxy";
        piece.m_enabled = true;
        piece.m_category = Piece.PieceCategory.Misc;
        piece.m_groundOnly = false;
        piece.m_groundPiece = false;
        piece.m_clipGround = false;
        piece.m_clipEverything = true;
        piece.m_allowAltGroundPlacement = true;
        piece.m_canBeRemoved = true;
        piece.m_noClipping = true;
        piece.m_resources = Array.Empty<Piece.Requirement>();

        prefab.AddComponent<ZoneSaviorTerrainProxy>();
        piece.m_icon = GetIcon(slope, reset, paint, paintReset);
        piece.m_description = GetPieceDescription(prefab);
        return prefab;
    }
}
